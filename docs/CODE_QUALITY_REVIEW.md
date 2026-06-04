# Code Quality Review — issue #40

**Scope:** `Form1.vb` (the ~9.7k-line core) and the §258–260 partial-class additions, reviewed
against the #40 checklist (code quality, comments/docs, structure). **Date:** 2026-06-04, branch
`ParaPerf`.

**Disposition (per the agreed scope):** produce a categorized findings summary and apply only the
**low-risk** fixes now (dead-code removal, stale-comment cleanup); leave structural refactors and
any change that touches the verified π math as tracked follow-ups. The code computes a bit-verified
5-billion-digit π, so "first, do no harm" outranks cosmetic cleanliness.

Severity key: **Critical** (correctness/safety) · **Major** (maintainability risk) · **Minor**
(style/consistency) · **Suggestion** (nice-to-have).

---

## Fixed in this pass (§261)

- **[Minor] Dead code — abandoned bump-allocator block.** ~78 commented-out lines (the old custom
  GMP memory pool: `VirtualAlloc`/`CopyMemory` `DllImport`s + `GmpAlloc`/`GmpRealloc`/`GmpFree`/
  `InitGmpPool`, annotated with `BUG:` notes) sat above the live allocator section. **Removed**,
  replaced by a 10-line rationale note that preserves *why* it was abandoned (violated GMP's
  free/realloc contract; `CInt` pool-offset overflow at 2 GB). The live `VirtualAlloc`/`VirtualFree`
  wrappers and `GmpNativeAlloc.dll` (#30) are unaffected. Pure-comment removal ⇒ zero behavioural
  risk; build clean.

---

## Findings (not changed — recommended as follow-ups)

### Critical
*None found.* The numerically-load-bearing paths are correct (bit-verified to 5 B) and the recent
audits (#71, §235 trace) covered the hot code. Thread-safety on shared state is handled deliberately
(`Volatile`/`Interlocked`/`SyncLock` around `_logLevel`, the GMP pool, the status hook).

### Major
1. **Method length / single responsibility.** `ComputePiGMP` (~1,000 lines) and `SafeMpzDiv` /
   `SafeMpzReciprocal` / `SafeMpzMul` are very large, mixing orchestration, checkpoint I/O, and inner
   math. This is the #1 maintainability cost. **Why not now:** decomposing them risks perturbing the
   verified math and the checkpoint/resume contract. **Follow-up:** extract *pure* leaf helpers
   (e.g. the §171/§218 quotient adjust, the §216 chunk loop) behind unit tests first, then lift —
   one stage at a time, each re-validated by a 1 B SHA check. (Companion to the #40 "file
   organisation" item: the §258–260 work already demonstrates the partial-class split pattern.)
2. **`Form1.vb` size (9.7k lines).** Logical sections (GMP P/Invoke wrappers, binary-split,
   reciprocal, sqrt, divide, decimal conversion, phase logging) could become partial-class files
   (`Form1.Gmp.vb`, `Form1.Reciprocal.vb`, …) with **no logic change** — a pure file-move. Lower
   risk than #1 but still churny; do it as a dedicated, diff-reviewable pass.

### Minor
3. **Magic number `1048576` (bytes→MB) — 47 sites.** Recommend a single
   `Private Const BYTES_PER_MB As Long = 1048576L` and migrate. **Why not now:** a 47-site sweep of
   the math file is churn the agreed scope excludes; it is mechanical and safe to do in its own
   commit. (The new §258–260 files use the literal too — migrate together.)
4. **Swallowed exceptions — ~45 bare `Catch`.** Most are *intentional* best-effort guards (logging
   sinks, UI `BeginInvoke`, telemetry, native cleanup) where throwing would crash a long run — these
   are correct and now mostly documented inline. **Action:** the few that are *not* obviously
   best-effort should log at level 1 (the §257 logging pass already moved allocator/OOM messages to
   level 1). No silent swallow on a correctness path was found.
5. **Section markers (`§NNN`).** ~260 fix-markers thread the file. They are invaluable for
   correlating code↔commit↔README↔memory and should **stay**, but a one-line index (marker → README
   section) would help newcomers. Captured here rather than inline.

### Suggestion
6. **Type-safety / casts.** Frequent `CInt`/`CLng`/`CULng` around the 64-bit limb arithmetic. These
   are deliberate (32-bit `mp_bitcnt_t` on Windows, GMP FFT limits) and several were hardened in
   §236/§237/§239. No unsafe narrowing cast was found; the existing `RemoveIntegerChecks` build flag
   means new casts must be reasoned about explicitly — keep documenting the 64-bit-safety intent at
   each site (as §236/§237 do).
7. **XML-doc on shared/public helpers.** The §258–260 additions use `''' <summary>`; the older
   `SafeMpz*` family relies on prose section headers. Adding `'''` summaries to the `SafeMpz*` and
   `BinarySplit*` entry points (params, preconditions, the over/under-estimate contracts) would aid
   tooling. Non-urgent.

---

## Checklist coverage (from #40)

| Area | Status |
|---|---|
| Naming conventions | OK — consistent PascalCase/camelCase; no rename needed. |
| Magic numbers | Finding #3 (BYTES_PER_MB) — deferred sweep. |
| Method length / SRP | Finding #1 — deferred (math-risk). |
| Error handling | Finding #4 — audited; best-effort swallows are intentional; level-1 errors landed in §257. |
| Dead code | **Fixed** (§261 allocator block). No other accidental dead code (the §138/§221 block is a deliberate "easy revert" reference). |
| Duplication | No significant un-factored duplication beyond the large-method internals (Finding #1). |
| Type safety | Finding #6 — casts are deliberate + documented. |
| Resource management | `Using` used for streams/readers; native buffers freed explicitly with matching alloc/free (§30). OK. |
| Thread safety | OK — `Volatile`/`Interlocked`/`SyncLock` on shared state. |
| Comment accuracy | Stale `§114` probe removed earlier (§256); no stale branch refs; no TODO/FIXME. |
| Diagnostic logging | **Done** — §252/§257 0–5 ladder gates every log; per-op dumps at level 5. |
| File organisation | Finding #2 — deferred partial-class split. |
| Constants centralised | `GMP_LARGE_THRESHOLD`, `SAFE`, `SAFE_LIMB_THRESHOLD`, `DISK_THRESHOLD` already named; Finding #3 is the remaining one. |
| GMP raw-pointer consistency | `GmpRaw_*` vs `gmp_lib` mix is intentional (raw P/Invoke on the hot path to skip marshalling, §108); documented. |

**Conclusion:** no Critical or correctness-affecting Major findings. The agreed low-risk fix (dead
allocator block, §261) is applied. The two Major items (large methods, file split) and the
BYTES_PER_MB sweep are real maintainability wins but are deliberately deferred to dedicated,
individually-validated commits so they cannot endanger the verified math — recommended order:
BYTES_PER_MB sweep → file-split (pure move) → leaf-helper extraction.
