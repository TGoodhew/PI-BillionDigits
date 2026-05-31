/* GmpNativeAlloc.c
 * Issue #30: Replace managed VirtualAlloc pool with native SLIST-based lock-free pool.
 *            GMP's alloc/realloc/free callbacks are installed as pure native C functions,
 *            eliminating the managed-delegate thunk (~40% CPU cost at 1B digits).
 * Issue #32B: GmpBatch_ComputeTerm / GmpBatch_CombineNodes — bundle multiple GMP
 *             arithmetic calls into single managed→native boundary crossings.
 *
 * Logging strategy (aggressive — every significant event is traced):
 *   LOG_NONE (0): silent
 *   LOG_INIT (1): DLL load, GMP function loads, Install/Flush calls, errors
 *   LOG_POOL (2): every pool hit/miss/eviction/flush block
 *   LOG_ALL  (3): every batch op entry/exit
 * Set via GmpNativeAlloc_LoadGmp(logLevel, ...).
 * Output: OutputDebugString (visible in DebugView/VS Output) + optional log file.
 */

#define WIN32_LEAN_AND_MEAN
#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <intrin.h>     /* _BitScanReverse64 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdarg.h>

/* ── Log levels ─────────────────────────────────────────────────────────────── */
#define LOG_NONE  0
#define LOG_INIT  1
#define LOG_POOL  2
#define LOG_ALL   3

static volatile LONG  g_logLevel    = LOG_INIT;
static char           g_logPath[MAX_PATH] = { 0 };
static CRITICAL_SECTION g_logLock;
static volatile LONG  g_logLockInit = 0;  /* 0=uninit, 1=init */

static void NativeLog(int level, const char* fmt, ...) {
    if (level > (int)g_logLevel) return;
    char buf[1024];
    va_list args;
    va_start(args, fmt);
    vsprintf_s(buf, sizeof(buf), fmt, args);
    va_end(args);
    OutputDebugStringA(buf);
    if (g_logPath[0] && g_logLockInit) {
        EnterCriticalSection(&g_logLock);
        FILE* f = NULL;
        if (fopen_s(&f, g_logPath, "a") == 0 && f) {
            fputs(buf, f);
            fclose(f);
        }
        LeaveCriticalSection(&g_logLock);
    }
}

/* ── GMP type definitions ─────────────────────────────────────────────────── */
/* __mpz_struct layout (GMP 6.x x64):
 *   offset 0:  int  _mp_alloc  (limbs allocated)
 *   offset 4:  int  _mp_size   (limbs used; sign = sign of number)
 *   offset 8:  void* _mp_d     (pointer to limb array)
 */

typedef void* (__cdecl *pfn_alloc)  (size_t);
typedef void* (__cdecl *pfn_realloc)(void*, size_t, size_t);
typedef void  (__cdecl *pfn_free)   (void*, size_t);
typedef void  (__cdecl *pfn_set_mem)(pfn_alloc, pfn_realloc, pfn_free);
typedef void  (__cdecl *pfn_get_mem)(pfn_alloc*, pfn_realloc*, pfn_free*);

/* GMP arithmetic function types */
typedef void (__cdecl *pfn_mpz_init)   (void*);
typedef void (__cdecl *pfn_mpz_clear)  (void*);
typedef void (__cdecl *pfn_mpz_set_ui) (void*, unsigned long);
typedef void (__cdecl *pfn_mpz_set_si) (void*, signed long);
typedef void (__cdecl *pfn_mpz_mul)    (void*, const void*, const void*);
typedef void (__cdecl *pfn_mpz_mul_ui) (void*, const void*, unsigned long);
typedef void (__cdecl *pfn_mpz_add)    (void*, const void*, const void*);
typedef void (__cdecl *pfn_mpz_add_ui) (void*, const void*, unsigned long);
typedef void (__cdecl *pfn_mpz_sub_ui) (void*, const void*, unsigned long);
typedef void (__cdecl *pfn_mpz_pow_ui) (void*, const void*, unsigned long);
typedef void (__cdecl *pfn_mpz_neg)    (void*, const void*);

/* Loaded from libgmp-10.dll */
static pfn_set_mem   g_gmp_set_memory_functions = NULL;
static pfn_get_mem   g_gmp_get_memory_functions = NULL;
static pfn_alloc     g_saved_alloc   = NULL;
static pfn_realloc   g_saved_realloc = NULL;
static pfn_free      g_saved_free    = NULL;
static pfn_mpz_init   g_mpz_init   = NULL;
static pfn_mpz_clear  g_mpz_clear  = NULL;
static pfn_mpz_set_ui g_mpz_set_ui = NULL;
static pfn_mpz_set_si g_mpz_set_si = NULL;
static pfn_mpz_mul    g_mpz_mul    = NULL;
static pfn_mpz_mul_ui g_mpz_mul_ui = NULL;
static pfn_mpz_add    g_mpz_add    = NULL;
static pfn_mpz_add_ui g_mpz_add_ui = NULL;
static pfn_mpz_sub_ui g_mpz_sub_ui = NULL;
static pfn_mpz_pow_ui g_mpz_pow_ui = NULL;
static pfn_mpz_neg    g_mpz_neg    = NULL;

static volatile LONG g_gmpLoaded = 0;  /* 1 after successful LoadGmp */

/* ── Pool configuration ──────────────────────────────────────────────────── */
/* Block layout: [ SLIST_ENTRY (16B on x64, 16B-aligned) | GMP data ... ]
 * VirtualAlloc gives page-aligned (>=16B) memory, so SLIST_ENTRY at offset 0 is
 * always correctly aligned.  GMP pointer = raw + GMP_BLOCK_PREFIX.
 * On free: raw = gmp_ptr - GMP_BLOCK_PREFIX; push raw onto SLIST bucket.          */
#define POOL_BUCKETS     64
#define POOL_CAP         256
#define POOL_MAX_BLOCK   (16LL * 1024 * 1024)  /* 16 MB — max pooled block size */
#define GMP_BLOCK_PREFIX sizeof(SLIST_ENTRY)   /* 16 bytes on x64               */

/* §223 (issue #47, 2026-05-22): per-CPU pool heads.
 *
 * Original layout: one global g_pool_heads[bucket] array. All 24 worker threads
 * competing on the same SLIST head causes the L3 cache line containing the head
 * pointer to bounce between cores on every push/pop — ~30-50 ns per atomic when
 * contended. At Phase 1 (BinarySplitChunk × 137K chunks at DOP=24) and during
 * post-§220 parallel SafeMpzMul recursion, the hot buckets (4 KB / 16 KB) saturate.
 *
 * New layout: pool_slot_t per [bucket][cpu_index], cache-line aligned (64 bytes).
 * Each thread tries its own CPU's local slot first; on miss it steals from a
 * neighbouring CPU's slot before falling back to VirtualAlloc.
 *
 * MAX_CPUS=32 covers the i9-12900K (24 threads) plus headroom. CPUs beyond
 * MAX_CPUS modulo down to a slot — degrades to cache-line contention with
 * another CPU but never corrupts.
 */
#define MAX_CPUS         32
typedef struct {
    SLIST_HEADER head;
    volatile LONG count;
    char pad[64 - sizeof(SLIST_HEADER) - sizeof(LONG)];
} pool_slot_t;
__declspec(align(64)) static pool_slot_t g_pool_slots[POOL_BUCKETS][MAX_CPUS];

/* Per-CPU local-vs-stolen hit stats (aggregated; not per-cpu) for diagnostics */
static volatile LONGLONG g_stat_hits_local  = 0;
static volatile LONGLONG g_stat_hits_steal  = 0;

/* Cache the CPU index, with modulo into MAX_CPUS. Inlinable, no Win32 thunk
 * overhead beyond GetCurrentProcessorNumber's ~ns cost. */
static __forceinline DWORD GetCpuSlot(void) {
    DWORD cpu = GetCurrentProcessorNumber();
    return cpu & (MAX_CPUS - 1);  /* MAX_CPUS=32 → mod via mask */
}

/* Aggregate stats (written via Interlocked; read at Flush for logging) */
static volatile LONGLONG g_stat_hits      = 0;
static volatile LONGLONG g_stat_misses    = 0;
static volatile LONGLONG g_stat_evictions = 0;
static volatile LONGLONG g_stat_large     = 0;
static volatile LONGLONG g_stat_alloc_fail = 0;

/* Returns the pool bucket index for byte size sz.
 * Bucket b covers (2^(b-1), 2^b].  Actual allocation = 2^b bytes.
 * Uses LZCNT/BSR instruction (single cycle on modern x64). */
static int PoolBucket(LONGLONG sz) {
    if (sz <= 1) return 0;
    ULONGLONG v = (ULONGLONG)(sz - 1);
    unsigned long idx = 0;
    _BitScanReverse64(&idx, v);          /* idx = floor(log2(v)) */
    int b = (int)(idx + 1);             /* b = ceil(log2(sz))    */
    return (b < POOL_BUCKETS) ? b : (POOL_BUCKETS - 1);
}

/* ── Native GMP allocator callbacks ─────────────────────────────────────── */

static void* __cdecl NativeAllocFunc(size_t sz) {
    LONGLONG lsz = (LONGLONG)(ULONGLONG)sz;
    if (lsz > 0 && lsz <= POOL_MAX_BLOCK) {
        int b = PoolBucket(lsz);
        DWORD cpu = GetCpuSlot();
        /* §223: try local CPU's slot first (uncontended in steady state) */
        PSLIST_ENTRY ent = InterlockedPopEntrySList(&g_pool_slots[b][cpu].head);
        if (ent) {
            InterlockedDecrement(&g_pool_slots[b][cpu].count);
            InterlockedIncrement64(&g_stat_hits);
            InterlockedIncrement64(&g_stat_hits_local);
            if ((int)g_logLevel >= LOG_POOL)
                NativeLog(LOG_POOL, "[NativePool] alloc HIT  b=%d cpu=%lu sz=%lld gmp=%p (local)\n",
                          b, cpu, lsz, (char*)ent + GMP_BLOCK_PREFIX);
            return (char*)ent + GMP_BLOCK_PREFIX;
        }
        /* §223: local pool empty — steal from neighbours before falling to VirtualAlloc.
         * Walk MAX_CPUS-1 victim slots; first success returns. */
        for (DWORD i = 1; i < MAX_CPUS; i++) {
            DWORD victim = (cpu + i) & (MAX_CPUS - 1);
            ent = InterlockedPopEntrySList(&g_pool_slots[b][victim].head);
            if (ent) {
                InterlockedDecrement(&g_pool_slots[b][victim].count);
                InterlockedIncrement64(&g_stat_hits);
                InterlockedIncrement64(&g_stat_hits_steal);
                if ((int)g_logLevel >= LOG_POOL)
                    NativeLog(LOG_POOL, "[NativePool] alloc HIT  b=%d cpu=%lu sz=%lld gmp=%p (stole from cpu=%lu)\n",
                              b, cpu, lsz, (char*)ent + GMP_BLOCK_PREFIX, victim);
                return (char*)ent + GMP_BLOCK_PREFIX;
            }
        }
        InterlockedIncrement64(&g_stat_misses);
        LONGLONG bucketSz = (1LL << b);
        void* raw = VirtualAlloc(NULL,
                                  (SIZE_T)(bucketSz + (LONGLONG)GMP_BLOCK_PREFIX),
                                  MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (!raw) {
            InterlockedIncrement64(&g_stat_alloc_fail);
            NativeLog(LOG_INIT, "[NativePool] ALLOC FAILED b=%d sz=%lld bucket=%lld\n",
                      b, lsz, bucketSz);
            return NULL;
        }
        if ((int)g_logLevel >= LOG_POOL)
            NativeLog(LOG_POOL, "[NativePool] alloc MISS b=%d sz=%lld raw=%p gmp=%p\n",
                      b, lsz, raw, (char*)raw + GMP_BLOCK_PREFIX);
        return (char*)raw + GMP_BLOCK_PREFIX;
    }
    /* Oversized (> 16 MB) or zero: allocate exactly, no pool */
    InterlockedIncrement64(&g_stat_large);
    void* raw = VirtualAlloc(NULL,
                              (SIZE_T)((ULONGLONG)sz + GMP_BLOCK_PREFIX),
                              MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!raw) {
        InterlockedIncrement64(&g_stat_alloc_fail);
        NativeLog(LOG_INIT, "[NativePool] OVERSIZED ALLOC FAILED sz=%lld\n", lsz);
        return NULL;
    }
    if ((int)g_logLevel >= LOG_POOL)
        NativeLog(LOG_POOL, "[NativePool] alloc LARGE sz=%lld raw=%p gmp=%p\n",
                  lsz, raw, (char*)raw + GMP_BLOCK_PREFIX);
    return (char*)raw + GMP_BLOCK_PREFIX;
}

static void __cdecl NativeFreeFunc(void* ptr, size_t sz) {
    if (!ptr) return;
    void* raw = (char*)ptr - GMP_BLOCK_PREFIX;
    LONGLONG lsz = (LONGLONG)(ULONGLONG)sz;
    if (lsz > 0 && lsz <= POOL_MAX_BLOCK) {
        int b = PoolBucket(lsz);
        DWORD cpu = GetCpuSlot();
        /* §223: push to local CPU's slot. POOL_CAP applies per-slot, so total
         * pooled capacity grows MAX_CPUS× — that's the design intent (each CPU
         * gets its own working set). */
        if (InterlockedIncrement(&g_pool_slots[b][cpu].count) <= POOL_CAP) {
            InterlockedPushEntrySList(&g_pool_slots[b][cpu].head, (PSLIST_ENTRY)raw);
            if ((int)g_logLevel >= LOG_POOL)
                NativeLog(LOG_POOL, "[NativePool] free RETURN b=%d cpu=%lu sz=%lld ptr=%p\n",
                          b, cpu, lsz, ptr);
            return;
        }
        /* Local slot full — roll back counter and fall through to VirtualFree */
        InterlockedDecrement(&g_pool_slots[b][cpu].count);
        InterlockedIncrement64(&g_stat_evictions);
    }
    /* Oversized, pool full, or zero: release immediately */
    VirtualFree(raw, 0, MEM_RELEASE);
    if ((int)g_logLevel >= LOG_POOL)
        NativeLog(LOG_POOL, "[NativePool] free EVICT sz=%lld ptr=%p\n", lsz, ptr);
}

static void* __cdecl NativeReallocFunc(void* ptr, size_t old_sz, size_t new_sz) {
    if (!ptr)    return NativeAllocFunc(new_sz);
    if (!new_sz) { NativeFreeFunc(ptr, old_sz); return NULL; }
    /* Same bucket → reuse in-place (bucket allocation >= actual size) */
    LONGLONG lo = (LONGLONG)(ULONGLONG)old_sz;
    LONGLONG ln = (LONGLONG)(ULONGLONG)new_sz;
    if (lo > 0 && lo <= POOL_MAX_BLOCK && ln > 0 && ln <= POOL_MAX_BLOCK) {
        if (PoolBucket(lo) == PoolBucket(ln)) {
            if ((int)g_logLevel >= LOG_POOL)
                NativeLog(LOG_POOL, "[NativePool] realloc IN-PLACE old=%lld new=%lld ptr=%p\n",
                          lo, ln, ptr);
            return ptr;
        }
    }
    void* np = NativeAllocFunc(new_sz);
    if (!np) return NULL;
    SIZE_T cp = (old_sz < new_sz) ? old_sz : new_sz;
    memcpy(np, ptr, cp);
    NativeFreeFunc(ptr, old_sz);
    if ((int)g_logLevel >= LOG_POOL)
        NativeLog(LOG_POOL, "[NativePool] realloc COPY old=%lld new=%lld old_ptr=%p new_ptr=%p\n",
                  lo, ln, ptr, np);
    return np;
}

/* ── Exported API ─────────────────────────────────────────────────────────── */

/* Load all GMP function pointers from the already-loaded libgmp-10.dll.
 * Must be called AFTER Math.Gmp.Native has loaded libgmp-10.dll but BEFORE
 * GmpNativeAlloc_Install().
 * logLevel: 0=none 1=init 2=pool 3=all
 * optLogPath: path for log file, or NULL/empty for OutputDebugString only.
 * Returns TRUE on success. */
__declspec(dllexport) BOOL WINAPI GmpNativeAlloc_LoadGmp(int logLevel, const char* optLogPath) {
    InterlockedExchange(&g_logLevel, (LONG)logLevel);
    if (optLogPath && optLogPath[0]) {
        strncpy_s(g_logPath, MAX_PATH, optLogPath, _TRUNCATE);
    }
    if (!g_logLockInit) {
        InitializeCriticalSection(&g_logLock);
        InterlockedExchange(&g_logLockInit, 1);
    }

    NativeLog(LOG_INIT, "[GmpNativeAlloc] LoadGmp START: logLevel=%d logPath='%s'\n",
              logLevel, g_logPath[0] ? g_logPath : "(none)");
    NativeLog(LOG_INIT, "[GmpNativeAlloc] GMP_BLOCK_PREFIX=%zu  POOL_BUCKETS=%d  POOL_CAP=%d  POOL_MAX_BLOCK=%lldMB  MAX_CPUS=%d (§223 per-CPU)\n",
              GMP_BLOCK_PREFIX, POOL_BUCKETS, POOL_CAP, POOL_MAX_BLOCK / (1024*1024), MAX_CPUS);

    /* §223: Initialise per-CPU SLIST pool headers (POOL_BUCKETS × MAX_CPUS) */
    for (int b = 0; b < POOL_BUCKETS; b++) {
        for (int c = 0; c < MAX_CPUS; c++) {
            InitializeSListHead(&g_pool_slots[b][c].head);
            InterlockedExchange(&g_pool_slots[b][c].count, 0);
        }
    }
    g_stat_hits = g_stat_misses = g_stat_evictions = g_stat_large = g_stat_alloc_fail = 0;
    g_stat_hits_local = g_stat_hits_steal = 0;

    /* Locate libgmp-10.dll (already loaded by Math.Gmp.Native) */
    HMODULE hGmp = GetModuleHandleA("libgmp-10.dll");
    if (!hGmp) {
        NativeLog(LOG_INIT, "[GmpNativeAlloc] ERROR: libgmp-10.dll not found in process — call after Math.Gmp.Native init\n");
        return FALSE;
    }
    NativeLog(LOG_INIT, "[GmpNativeAlloc] libgmp-10.dll HMODULE=%p\n", (void*)hGmp);

    /* Memory management */
    g_gmp_set_memory_functions = (pfn_set_mem)GetProcAddress(hGmp, "__gmp_set_memory_functions");
    g_gmp_get_memory_functions = (pfn_get_mem)GetProcAddress(hGmp, "__gmp_get_memory_functions");
    NativeLog(LOG_INIT, "[GmpNativeAlloc]   __gmp_set_memory_functions = %p\n", (void*)g_gmp_set_memory_functions);
    NativeLog(LOG_INIT, "[GmpNativeAlloc]   __gmp_get_memory_functions = %p\n", (void*)g_gmp_get_memory_functions);
    if (!g_gmp_set_memory_functions || !g_gmp_get_memory_functions) {
        NativeLog(LOG_INIT, "[GmpNativeAlloc] ERROR: could not load GMP memory management functions\n");
        return FALSE;
    }

    /* Arithmetic functions for batch ops */
#define LOAD_GMP(fn_name, var_name) \
    var_name = (pfn_mpz_##fn_name)GetProcAddress(hGmp, "__gmpz_" #fn_name); \
    NativeLog(LOG_INIT, "[GmpNativeAlloc]   __gmpz_" #fn_name " = %p\n", (void*)var_name); \
    if (!var_name) { NativeLog(LOG_INIT, "[GmpNativeAlloc] ERROR: __gmpz_" #fn_name " not found\n"); return FALSE; }

    LOAD_GMP(init,   g_mpz_init)
    LOAD_GMP(clear,  g_mpz_clear)
    LOAD_GMP(set_ui, g_mpz_set_ui)
    LOAD_GMP(set_si, g_mpz_set_si)
    LOAD_GMP(mul,    g_mpz_mul)
    LOAD_GMP(mul_ui, g_mpz_mul_ui)
    LOAD_GMP(add,    g_mpz_add)
    LOAD_GMP(add_ui, g_mpz_add_ui)
    LOAD_GMP(sub_ui, g_mpz_sub_ui)
    LOAD_GMP(pow_ui, g_mpz_pow_ui)
    LOAD_GMP(neg,    g_mpz_neg)
#undef LOAD_GMP

    InterlockedExchange(&g_gmpLoaded, 1);
    NativeLog(LOG_INIT, "[GmpNativeAlloc] LoadGmp OK — 11 arithmetic + 2 memory functions loaded\n");
    return TRUE;
}

/* Install native pool allocators into GMP's memory function table.
 * Saves the existing (CRT malloc) allocators first so they can be used for small
 * allocs if needed.  Call after GmpNativeAlloc_LoadGmp(). */
__declspec(dllexport) void WINAPI GmpNativeAlloc_Install(void) {
    if (!g_gmp_set_memory_functions || !g_gmp_get_memory_functions) {
        NativeLog(LOG_INIT, "[GmpNativeAlloc] Install ERROR: LoadGmp not called or failed\n");
        return;
    }
    /* Save current GMP allocators (CRT malloc/realloc/free from default init) */
    g_gmp_get_memory_functions(&g_saved_alloc, &g_saved_realloc, &g_saved_free);
    NativeLog(LOG_INIT, "[GmpNativeAlloc] Install: saved alloc=%p realloc=%p free=%p\n",
              (void*)g_saved_alloc, (void*)g_saved_realloc, (void*)g_saved_free);

    /* Install native pool callbacks */
    g_gmp_set_memory_functions(NativeAllocFunc, NativeReallocFunc, NativeFreeFunc);
    NativeLog(LOG_INIT, "[GmpNativeAlloc] Install OK: NativeAllocFunc=%p NativeReallocFunc=%p NativeFreeFunc=%p\n",
              (void*)NativeAllocFunc, (void*)NativeReallocFunc, (void*)NativeFreeFunc);
}

/* Flush all pooled blocks back to OS.  Call between Phase 2 levels and after
 * the full computation.  Does NOT affect blocks currently held by live GMP objects. */
__declspec(dllexport) void WINAPI GmpNativeAlloc_Flush(void) {
    /* §223: include per-CPU local-vs-steal hit ratio so we can see whether
     * work-stealing is firing frequently (high steal % → work imbalance). */
    NativeLog(LOG_INIT,
              "[GmpNativeAlloc] Flush: stats hits=%lld (local=%lld steal=%lld) misses=%lld evictions=%lld large=%lld alloc_fail=%lld\n",
              g_stat_hits, g_stat_hits_local, g_stat_hits_steal,
              g_stat_misses, g_stat_evictions, g_stat_large, g_stat_alloc_fail);

    LONGLONG total = 0;
    /* §223: walk all [bucket][cpu] slots */
    for (int b = 0; b < POOL_BUCKETS; b++) {
        int cnt = 0;
        for (int c = 0; c < MAX_CPUS; c++) {
            PSLIST_ENTRY ent;
            while ((ent = InterlockedPopEntrySList(&g_pool_slots[b][c].head)) != NULL) {
                InterlockedDecrement(&g_pool_slots[b][c].count);
                VirtualFree(ent, 0, MEM_RELEASE);
                cnt++;
                total++;
            }
        }
        if (cnt > 0 && (int)g_logLevel >= LOG_POOL)
            NativeLog(LOG_POOL, "[GmpNativeAlloc] Flush bucket %d: freed %d blocks (all CPUs)\n", b, cnt);
    }
    NativeLog(LOG_INIT, "[GmpNativeAlloc] Flush complete: %lld pool blocks freed\n", total);
}

/* §241 (issue #69): census of currently-pooled memory.
 * Sums count × bucketSize across all [bucket][cpu] slots.  The pool only ever holds
 * blocks <= POOL_MAX_BLOCK (16 MB), so the total is bounded by the small/medium
 * buckets that GMP churns (FFT temporaries etc.), NOT the multi-GB buffers (those
 * take the oversized path and are VirtualFree'd immediately, never pooled).
 * Uses the per-slot count fields (cheap, no SLIST walk); accurate at quiescent
 * phase boundaries.  Logs a per-bucket breakdown at LOG_INIT. */
__declspec(dllexport) void WINAPI GmpNativeAlloc_PoolCensus(ULONGLONG* outBytes, ULONG* outBlocks) {
    ULONGLONG bytes = 0;
    ULONG blocks = 0;
    for (int b = 0; b < POOL_BUCKETS; b++) {
        LONGLONG cnt = 0;
        for (int c = 0; c < MAX_CPUS; c++) cnt += g_pool_slots[b][c].count;
        if (cnt > 0) {
            ULONGLONG bb = (ULONGLONG)cnt * (1ULL << b);
            bytes += bb;
            blocks += (ULONG)cnt;
            if ((int)g_logLevel >= LOG_INIT)
                NativeLog(LOG_INIT, "[PoolCensus]   bucket %d (%lluB ea): %lld blocks, %.1f MB\n",
                          b, (1ULL << b), cnt, bb / (1024.0 * 1024.0));
        }
    }
    if (outBytes)  *outBytes  = bytes;
    if (outBlocks) *outBlocks = blocks;
    NativeLog(LOG_INIT, "[PoolCensus] TOTAL pooled: %u blocks, %.3f GB\n",
              blocks, bytes / (1024.0 * 1024.0 * 1024.0));
}

/* §241 (issue #69): release pooled blocks whose bucket size >= minBucketBytes back
 * to the OS via VirtualFree, returning the count and bytes freed.  Buckets smaller
 * than minBucketBytes are preserved (small buckets have high reuse; trimming them is
 * counter-productive).  NOTE: the pool only holds blocks <= POOL_MAX_BLOCK (16 MB),
 * so minBucketBytes > 16 MB is a no-op — pass <= 16 MB (e.g. 1 MB) to free anything. */
__declspec(dllexport) void WINAPI GmpNativeAlloc_Trim(
    ULONGLONG minBucketBytes, ULONG* outBuffersFreed, ULONGLONG* outBytesFreed) {
    LONGLONG freedBlocks = 0;
    ULONGLONG freedBytes = 0;
    for (int b = 0; b < POOL_BUCKETS; b++) {
        ULONGLONG bucketSz = (1ULL << b);
        if (bucketSz < minBucketBytes) continue;   /* keep small high-reuse buckets */
        for (int c = 0; c < MAX_CPUS; c++) {
            PSLIST_ENTRY ent;
            while ((ent = InterlockedPopEntrySList(&g_pool_slots[b][c].head)) != NULL) {
                InterlockedDecrement(&g_pool_slots[b][c].count);
                VirtualFree(ent, 0, MEM_RELEASE);
                freedBlocks++;
                freedBytes += bucketSz;
            }
        }
    }
    if (outBuffersFreed) *outBuffersFreed = (ULONG)freedBlocks;
    if (outBytesFreed)   *outBytesFreed   = freedBytes;
    NativeLog(LOG_INIT, "[GmpNativeAlloc_Trim] minBucket=%lluB (%.1f MB): freed %lld blocks, %.3f GB to OS\n",
              minBucketBytes, minBucketBytes / (1024.0 * 1024.0),
              freedBlocks, freedBytes / (1024.0 * 1024.0 * 1024.0));
}

/* Free a GMP limb block that was allocated by the native pool.
 * ptr is the GMP data pointer (raw = ptr - GMP_BLOCK_PREFIX).
 * Called from managed code for blocks that SafeMpzMul evicts directly. */
__declspec(dllexport) void WINAPI GmpNativeAlloc_FreeRaw(void* ptr, LONGLONG sz) {
    if (!ptr) return;
    NativeLog(LOG_POOL, "[GmpNativeAlloc] FreeRaw ptr=%p sz=%lld\n", ptr, sz);
    NativeFreeFunc(ptr, (size_t)sz);
}

/* Pool get/return exposed for managed code (used during transition period). */
__declspec(dllexport) void* WINAPI GmpNativeAlloc_PoolGet(LONGLONG sz) {
    return NativeAllocFunc((size_t)sz);
}
__declspec(dllexport) void WINAPI GmpNativeAlloc_PoolReturn(void* ptr, LONGLONG sz) {
    NativeFreeFunc(ptr, (size_t)sz);
}

/* ── GmpBatch_ComputeTerm ────────────────────────────────────────────────── */
/* Compute one Chudnovsky Ramanujan binary-split base case for term index a.
 * pPtr, qPtr, tPtr: pointers to pre-initialised __mpz_struct results (P, Q, T).
 * c3Ptr:           pointer to pre-initialised constant C^3 = 262537412640768000.
 * Replaces 11+ individual P/Invoke calls with a single managed→native crossing.
 *
 * Chudnovsky formulas (a >= 1):
 *   P(a) = (6a-5)(2a-1)(6a-1)
 *   Q(a) = a^3 * C^3
 *   T(a) = P(a) * (13591409 + 545140134*a) * (-1)^a                          */
__declspec(dllexport) void WINAPI GmpBatch_ComputeTerm(
    LONGLONG    a,
    void*       pPtr,
    void*       qPtr,
    void*       tPtr,
    void*       c3Ptr)
{
    if (!g_gmpLoaded) {
        NativeLog(LOG_INIT, "[GmpBatch_ComputeTerm] ERROR: GMP not loaded (call LoadGmp first)\n");
        return;
    }
    NativeLog(LOG_ALL, "[GmpBatch_ComputeTerm] a=%lld entry\n", a);

    /* Allocate temporary mpz_t structs on the stack (headers only; limbs via pool) */
    char aBig_s[16], t1_s[16], t2_s[16];
    void* aBig = aBig_s;
    void* t1   = t1_s;
    void* t2   = t2_s;
    memset(aBig, 0, 16);
    memset(t1,   0, 16);
    memset(t2,   0, 16);
    g_mpz_init(aBig);
    g_mpz_init(t1);
    g_mpz_init(t2);

    /* aBig = a  (a fits in signed long: max ~360M for 5B digits << INT32_MAX) */
    g_mpz_set_si(aBig, (long)(LONGLONG)a);

    /* t1 = 6a - 5 */
    g_mpz_mul_ui(t1, aBig, 6UL);
    g_mpz_sub_ui(t1, t1,   5UL);

    /* t2 = 2a - 1 */
    g_mpz_mul_ui(t2, aBig, 2UL);
    g_mpz_sub_ui(t2, t2,   1UL);

    /* P = t1 * t2 = (6a-5)(2a-1) */
    g_mpz_mul(pPtr, t1, t2);

    /* t1 = 6a - 1 */
    g_mpz_mul_ui(t1, aBig, 6UL);
    g_mpz_sub_ui(t1, t1,   1UL);

    /* P = P * t1 = (6a-5)(2a-1)(6a-1) */
    g_mpz_mul(pPtr, pPtr, t1);

    /* Q = a^3 * C3 */
    g_mpz_pow_ui(qPtr, aBig, 3UL);
    g_mpz_mul(qPtr, qPtr, c3Ptr);

    /* t1 = 545140134*a + 13591409 */
    g_mpz_mul_ui(t1, aBig, 545140134UL);
    g_mpz_add_ui(t1, t1,   13591409UL);

    /* T = P * t1 */
    g_mpz_mul(tPtr, pPtr, t1);

    /* Negate T if a is odd */
    if (a & 1LL) {
        g_mpz_neg(tPtr, tPtr);
    }

    g_mpz_clear(t2);
    g_mpz_clear(t1);
    g_mpz_clear(aBig);

    NativeLog(LOG_ALL, "[GmpBatch_ComputeTerm] a=%lld done\n", a);
}

/* ── GmpBatch_CombineNodes ───────────────────────────────────────────────── */
/* Combine two binary-split nodes (left and right) into a result node.
 * All pointers are pre-initialised __mpz_struct.
 * Replaces 5 individual P/Invoke calls with a single boundary crossing.
 *
 *   res.P = left.P * right.P
 *   res.Q = left.Q * right.Q
 *   tempA = left.T * right.Q
 *   tempB = left.P * right.T
 *   res.T = tempA + tempB                                                      */
__declspec(dllexport) void WINAPI GmpBatch_CombineNodes(
    void* resPPtr, void* resQPtr, void* resTPtr,
    void* lPPtr,   void* lQPtr,   void* lTPtr,
    void* rPPtr,   void* rQPtr,   void* rTPtr,
    void* tempAPtr, void* tempBPtr)
{
    if (!g_gmpLoaded) {
        NativeLog(LOG_INIT, "[GmpBatch_CombineNodes] ERROR: GMP not loaded (call LoadGmp first)\n");
        return;
    }
    NativeLog(LOG_ALL, "[GmpBatch_CombineNodes] entry\n");

    g_mpz_mul(resPPtr, lPPtr, rPPtr);   /* res.P = left.P * right.P */
    g_mpz_mul(resQPtr, lQPtr, rQPtr);   /* res.Q = left.Q * right.Q */
    g_mpz_mul(tempAPtr, lTPtr, rQPtr);  /* tempA = left.T * right.Q */
    g_mpz_mul(tempBPtr, lPPtr, rTPtr);  /* tempB = left.P * right.T */
    g_mpz_add(resTPtr, tempAPtr, tempBPtr); /* res.T = tempA + tempB */

    NativeLog(LOG_ALL, "[GmpBatch_CombineNodes] done\n");
}

/* ── DllMain ─────────────────────────────────────────────────────────────── */
BOOL WINAPI DllMain(HINSTANCE hinstDLL, DWORD fdwReason, LPVOID lpvReserved) {
    (void)hinstDLL;
    (void)lpvReserved;
    switch (fdwReason) {
        case DLL_PROCESS_ATTACH:
            /* Disable per-thread DLL_THREAD_ATTACH/DETACH — not needed */
            DisableThreadLibraryCalls(hinstDLL);
            /* NativeLog not usable yet (logLockInit=0 and no logPath set);
             * OutputDebugString directly for early attach trace.            */
            OutputDebugStringA("[GmpNativeAlloc] DLL_PROCESS_ATTACH\n");
            break;
        case DLL_PROCESS_DETACH:
            /* Flush pool on unload — only during process exit (lpvReserved==NULL means
             * FreeLibrary; lpvReserved!=NULL means process terminating, skip flush). */
            if (!lpvReserved) {
                GmpNativeAlloc_Flush();
            }
            if (g_logLockInit) {
                DeleteCriticalSection(&g_logLock);
                InterlockedExchange(&g_logLockInit, 0);
            }
            break;
    }
    return TRUE;
}
