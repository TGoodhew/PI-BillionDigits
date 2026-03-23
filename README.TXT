================================================================================
  PI-BillionDigits — Code Change Log
  Documents all post-initial-development modifications and their rationale.
================================================================================

--------------------------------------------------------------------------------
OVERVIEW
--------------------------------------------------------------------------------

This document records every substantive change made to the codebase after the
initial implementation of the Chudnovsky binary-splitting pi computation.
Changes are grouped by area and ordered chronologically within each group.

Files modified:
  Form1.vb
  ApplicationEvents.vb
  PI-BillionDigits.vbproj  (README.TXT added as project item)


================================================================================
SECTION 1 — CRASH REPORTING & EXCEPTION VISIBILITY
================================================================================

Problem
-------
The application was crashing silently with no error information displayed or
written anywhere. When a crash occurred, the process terminated without any
indication of the exception type, message, or call stack.

--------------------------------------------------------------------------------
1.1  ApplicationEvents.vb — MyApplication_UnhandledException handler
--------------------------------------------------------------------------------

What changed:
  Added implementation of the MyApplication_UnhandledException event handler
  inside the MyApplication partial class. Previously the class body was empty
  (the event was only referenced in a comment).

Why:
  The VB application framework fires MyApplication.UnhandledException for any
  managed exception that propagates to the top of the call stack without being
  caught. With an empty MyApplication class, this event had no handler and the
  process terminated silently.

What it does now:
  - Builds a full exception message including the exception type, message, and
    stack trace, walking the entire InnerException chain.
  - Writes the full detail to the log file (c:\PiOutput\pi_phase_log.txt)
    BEFORE showing the message box, so a record survives even if the message
    box interaction is interrupted.
  - Shows a modal error dialog with the full text.
  - Sets e.ExitApplication = True so the process exits cleanly after the dialog.

--------------------------------------------------------------------------------
1.2  Form1.vb — AppDomain.CurrentDomain.UnhandledException subscription
--------------------------------------------------------------------------------

What changed:
  Added a subscription to AppDomain.CurrentDomain.UnhandledException in
  Form1_Load, along with the OnAppDomainUnhandledException handler method.

Why:
  The VB ApplicationEvents handler covers managed exceptions in the main
  application thread. The AppDomain handler covers exceptions on background
  threads (including the 256 MB compute thread) and certain CLR-level events
  that do not pass through the VB application framework. The two handlers
  complement each other without duplicating work.

What it does now:
  - If the exception object is a managed Exception, calls WriteExceptionToLog
    with the full chain.
  - If it is a non-Exception object (rare CLR internal events), logs its type
    name.
  - Logs the IsTerminating flag so the log records whether the process was
    going to exit regardless.

--------------------------------------------------------------------------------
1.3  Form1.vb — Native crash handler (SetUnhandledExceptionFilter)
--------------------------------------------------------------------------------

What changed:
  Added a P/Invoke declaration of the Win32 SetUnhandledExceptionFilter
  function, a NativeCrashFilterCallback delegate, a _nativeCrashCallback
  field (kept as an instance field to prevent GC collection), and the
  HandleNativeCrash callback method. The filter is registered in Form1_Load.

Why:
  GMP is a native C library. If it encounters an internal error — for example
  a heap corruption, a failed assertion, or a call to abort() — the failure
  occurs entirely within native code and never becomes a managed .NET exception.
  The .NET CLR never sees it, so AppDomain.UnhandledException and the VB
  ApplicationEvents handler are never invoked. The process simply terminates.

  SetUnhandledExceptionFilter installs a Win32-level callback that Windows
  calls as a last resort before the process dies, even for native SEH
  exceptions, abort(), and stack overflows that originate in native code.

What it does now:
  - Appends a timestamped "NATIVE CRASH — process terminating" marker to the
    log file.
  - Includes a pointer to the preceding log entries as the location of the
    failing GMP call (see Section 2 for the per-operation trace logging).
  - Returns EXCEPTION_CONTINUE_SEARCH (0) so Windows can still write a crash
    dump via Windows Error Reporting.

Limitation:
  In .NET 5+ the CLR installs its own exception filter and may override the
  application's filter for some exception types. The handler is therefore
  best-effort for truly native crashes. For managed exceptions that are
  marshaled back from P/Invoke boundaries, the AppDomain handler (1.2) is
  more reliable.

Note on _nativeCrashCallback field:
  The delegate must be held in a strong reference (instance field) for the
  lifetime of the process. If it were a local variable it would be collected
  by the GC and the callback pointer would become a dangling pointer, causing
  a crash when Windows attempts to invoke it.


================================================================================
SECTION 2 — DETAILED COMPUTATION LOGGING
================================================================================

Problem
-------
The existing logging (LogPhase calls) showed coarse phase transitions but gave
no information about which specific operation or data item was in progress when
a crash occurred. After a silent crash, the log provided no actionable location.

--------------------------------------------------------------------------------
2.1  Form1.vb — WriteToLog helper
--------------------------------------------------------------------------------

What changed:
  Added a new private WriteToLog(message As String) method.

Why:
  LogPhase serves a dual purpose: it writes to the log file AND updates the UI
  list box via BeginInvoke. This makes it unsuitable for use from exception
  handlers (UI may be in an inconsistent state) or from background threads
  where excessive BeginInvoke calls would flood the message queue.

  WriteToLog writes only to the file — no UI interaction — making it safe to
  call from any thread at any time, including from within exception handlers
  and crash callbacks.

What it does:
  Each entry includes:
    - Timestamp to millisecond precision (yyyy-MM-dd HH:mm:ss.fff)
    - Managed thread ID (T<n>) to identify which thread generated the entry
    - Elapsed time since computation started (hh:mm:ss.fff)
    - Current process working set in MB
    - The message text

  File.AppendAllText opens, writes, and closes the file synchronously on each
  call. This guarantees the entry is physically on disk before execution
  continues — meaning the last entry in the log before a crash identifies the
  operation that crashed the process, even for native crashes that give no
  other warning.

--------------------------------------------------------------------------------
2.2  Form1.vb — WriteExceptionToLog helper
--------------------------------------------------------------------------------

What changed:
  Added a new private WriteExceptionToLog(context As String, ex As Exception)
  method.

Why:
  Multiple catch blocks previously showed MessageBox.Show with ex.Message and
  ex.StackTrace but wrote nothing to the log file. For a crash that occurs on
  the background compute thread with no visible UI, the MessageBox might never
  appear (the thread terminates and the main window is still in a responsive
  state with no dialog). Writing to the log file ensures the information is
  preserved regardless.

What it does:
  - Walks the full InnerException chain (important for P/Invoke failures which
    often wrap the root cause inside one or more InnerExceptions).
  - Writes the exception type, message, and stack trace for each level.
  - Calls WriteToLog so the output includes the standard timestamp/thread
    header and is physically flushed to disk immediately.

--------------------------------------------------------------------------------
2.3  Form1.vb — Per-operation trace logging
--------------------------------------------------------------------------------

What changed:
  Added WriteToLog calls at the following locations:

  BinarySplitChunk:
    - On entry: logs the (a, b) range and term count for the chunk.
    - On exit: logs the (a, b) range and the computed stack depth.
    - In the exception catch block: logs the exact WorkItem (a, b, isComplete)
      that was being processed when the exception occurred.

  SerializeNodeToDisk:
    - Before writing: logs the filename being written.
    - After writing: logs the filename and resulting file size in KB.

  LoadNodeFromDisk:
    - Before reading: logs the filename and file size in KB.

  BinarySplitGMP:
    - Every 100 chunks during the streaming phase (was: every 1,000).

  ComputePiGMP (final computation steps):
    - Before each major GMP operation: logs the operation name and, where
      meaningful, the digit count of the operands.
    - Operations covered: mpz_ui_pow_ui, mpz_mul (x2), mpz_mul_ui (x2),
      mpz_sqrt, mpz_tdiv_q, mpz_get_str.

  Node size logging:
    - After BinarySplitGMP completes: logs the approximate digit count of the
      P, Q, and T components of each top-level node.

Why:
  BinarySplitChunk is called once per chunk (~137,000 times for 1 billion
  digits). If a GMP operation inside any chunk fails, the entry/exit log
  immediately identifies which chunk range was active. The WorkItem logged in
  the catch block narrows it further to the specific binary-split node.

  The per-GMP-operation logs in ComputePiGMP fire only 8–10 times per run but
  are the most precise indicator of where the final computation fails, since
  each entry is written to disk before the GMP call it precedes.

--------------------------------------------------------------------------------
2.4  Form1.vb — Logging mode recorded in log file header
--------------------------------------------------------------------------------

What changed:
  BtnCompute_Click now writes a "Logging:" line to the log file header
  indicating whether DETAILED_LOGGING was compiled in.

Why:
  When reading a log file from a past run, it is not obvious whether the
  absence of detailed entries means the crash happened before the first
  detailed entry, or that detailed logging was simply disabled. The header
  line removes this ambiguity.


================================================================================
SECTION 3 — DETAILED LOGGING COMPILE-TIME TOGGLE (#Const DETAILED_LOGGING)
================================================================================

Problem
-------
Detailed per-operation logging (BinarySplitChunk enter/exit, serialize/
deserialize per file, ComputePiGMP per-GMP-op) generates very high I/O volume
for large computations — approximately 2 × 137,000 + 2 × 137,000 + several
hundred thousand more file writes for a 1 billion digit run. This slows down
the computation and makes the log file very large during normal operation.

Change
------
Added a compile-time constant at the top of Form1.vb:

    #Const DETAILED_LOGGING = True

All high-volume WriteToLog calls are wrapped with:

    #If DETAILED_LOGGING Then
        WriteToLog(...)
    #End If

This is the VB equivalent of C's:

    #define DETAILED_LOGGING
    #ifdef DETAILED_LOGGING
        log(...)
    #endif

Setting the constant to False removes the wrapped code from the compiled
binary entirely — there is zero runtime cost. The FileInfo stat calls that
are only needed to report file sizes are also inside the conditional blocks
so they too are eliminated when detailed logging is off.

What is always logged (regardless of setting):
  - All LogPhase calls (phase transitions with timestamps, elapsed time, RAM)
  - All WriteExceptionToLog calls (full exception chains)
  - AppDomain unhandled exception events
  - Native crash filter callback marker
  - displayStr memory release confirmation

What is conditional on DETAILED_LOGGING = True:
  - BinarySplitChunk enter/exit per chunk (~137K per billion digits)
  - SerializeNodeToDisk "Writing" and "Done" per file (~137K per billion)
  - LoadNodeFromDisk "Loading" per file (many per billion digits)
  - ComputePiGMP individual GMP operation steps (~8–10 per run)
  - Top-level node P/Q/T digit size report (1–2 per run)

Recommendation:
  Keep DETAILED_LOGGING = True while investigating crashes. Switch to False
  once the computation runs stably to reduce log file size and I/O overhead.


================================================================================
SECTION 4 — MEMORY FRAGMENTATION FIXES
================================================================================

Background
----------
The original code used patterns that caused two kinds of memory fragmentation
in the .NET runtime:

  Managed heap fragmentation: pinned objects and Large Object Heap (LOH)
  allocations prevent the GC from compacting memory. Over a long computation,
  the address space becomes Swiss cheese — nominally free but unusable for
  new large allocations. This manifests as OutOfMemoryException even when
  Task Manager shows available RAM.

  Allocation pressure: hundreds of thousands of short-lived objects flowing
  through Gen0/Gen1 force frequent GC collections, each more expensive than
  the last as surviving objects are promoted.

Eight specific issues were identified and fixed:

--------------------------------------------------------------------------------
4.1  Pinned byte arrays in LoadNodeFromDisk  [CRITICAL]
--------------------------------------------------------------------------------

Original code:
  Dim pHandle As GCHandle = GCHandle.Alloc(pBytes, GCHandleType.Pinned)
  gmp_lib.mpz_import(...)
  pHandle.Free()

Problem:
  GCHandle.Alloc(Pinned) tells the GC it must not move this array. The GC
  cannot compact the heap segment containing a pinned object — objects on
  either side become stranded, creating permanent holes. Pinning three arrays
  per node load × ~137,000 loads per level × ~17 levels = millions of pin/
  unpin cycles. Even after unpinning, the fragmentation pattern persists
  because the GC cannot retroactively compact around where the pins were.

  More critically: if mpz_import throws an exception, execution jumps
  directly to the Catch block. pHandle.Free() is never called. The array
  stays pinned for the life of the process. This is a guaranteed leak for
  any error path through this function.

Fix:
  Replaced GCHandle.Alloc(Pinned) with Marshal.AllocHGlobal / FreeHGlobal.
  Marshal.AllocHGlobal allocates directly from the native (unmanaged) heap.
  This memory is completely invisible to the .NET GC — it cannot fragment
  the managed heap because it is not part of it. The Free call is in a
  Finally block, so it cannot leak even if an exception occurs.

  New helper method DeserializeOneMpz encapsulates the pattern.

--------------------------------------------------------------------------------
4.2  Large Object Heap allocations in SerializeNodeToDisk and LoadNodeFromDisk
     [CRITICAL]
--------------------------------------------------------------------------------

Original code (serialize):
  Dim pBytes(pLen - 1) As Byte
  Marshal.Copy(pPtr.ToIntPtr(), pBytes, 0, pLen)
  bw.Write(pBytes)

Original code (deserialize):
  Dim pBytes() As Byte = br.ReadBytes(pLen)
  ' then pinned via GCHandle.Alloc

Problem:
  Any byte array larger than 85 KB is allocated on the Large Object Heap.
  The LOH is never compacted by default in .NET — freed LOH slots remain as
  holes until a future allocation happens to fit exactly. For a billion-digit
  computation, the P, Q, and T components of each node are hundreds of MB
  of raw bytes. Allocating six such arrays per combine pair (three for
  serialization, three for deserialization) and then releasing them carves
  the LOH into permanently fragmented segments.

Fix:
  Replaced per-field managed arrays with a single 64 KB staging buffer
  (Dim staging(65535) As Byte). At 64 KB this is well below the 85 KB LOH
  threshold, so it always lives in the Small Object Heap and is freely
  compactable. The buffer is declared once per call to SerializeNodeToDisk /
  LoadNodeFromDisk and reused for all three fields (P, Q, T) via the helper
  methods SerializeOneMpz and DeserializeOneMpz.

  For serialization, the GMP-allocated native buffer (from mpz_export) is
  walked in 64 KB chunks through the staging buffer and written to the
  BinaryWriter without ever copying the full number into a managed array.

  For deserialization, bytes are read from the BinaryReader in 64 KB chunks
  into the staging buffer, then marshaled into the unmanaged destination
  buffer (see 4.1). Again, the full number never exists as a managed array.

--------------------------------------------------------------------------------
4.3  mpz_t managed wrapper object churn  [HIGH]
--------------------------------------------------------------------------------

Problem:
  Every Chudnovsky series term creates 6 New mpz_t() wrappers (aBig, t1,
  t2, res.P, res.Q, res.T). For a 512-term chunk that is ~3,072 allocations.
  With ~137,000 chunks for a billion digits, approximately 420 million small
  managed objects flow through Gen0/Gen1 during the chunk phase alone. The
  combine phase adds 5 more per pair at each level.

  While the GC can handle this volume, the constant promotion pressure means
  GC collections are frequent and increasingly expensive as objects survive
  into higher generations.

Fix:
  This was addressed indirectly by the Tuple and DiskNode changes (4.4, 4.6)
  which reduce the surrounding allocation pressure. The core mpz_t wrapping
  pattern inside BinarySplitChunk was preserved because restructuring it
  would require pooling GMP integers across calls — a risky change that could
  introduce double-free bugs in the complex stack-based splitting logic.

--------------------------------------------------------------------------------
4.4  DiskNode declared as Class instead of Structure  [HIGH]
--------------------------------------------------------------------------------

Original code:
  Private Class DiskNode
      Public FilePath As String
      ...
  End Class

Problem:
  DiskNode is a Class (reference type). Each DiskNode instance is a separate
  heap allocation. For ~137,000 chunks, the diskNodes list held ~137,000
  individual heap objects. When "diskNodes = nextDiskNodes" executes at the
  end of each combine level, the old list's ~137,000 DiskNode objects all
  become garbage simultaneously — a "mass death" event that triggers a large
  Gen1/Gen2 collection. This pattern repeats at every combine level (~17
  times for 1 billion digits).

Fix:
  Changed DiskNode from Class to Structure. Value types stored in a
  List(Of DiskNode) live inside the list's internal array as contiguous
  memory — there are no individual heap allocations per node, and the entire
  set of 137,000 nodes occupies one contiguous block. When the list is
  replaced, only one object (the List wrapper) becomes garbage, not 137,000.

  As part of this change, the MemoryNode As Tuple(Of mpz_t,mpz_t,mpz_t)
  field was replaced by three separate fields MemP, MemQ, MemT (see 4.6).

--------------------------------------------------------------------------------
4.5  Unsized Stack(Of WorkItem) and Dictionary(Of Integer, Result) in
     BinarySplitChunk  [MEDIUM]
--------------------------------------------------------------------------------

Original code:
  Dim workStack As New Stack(Of WorkItem)()
  Dim results As New Dictionary(Of Integer, Result)()

Problem:
  Both collections start with a small default capacity and resize by
  doubling as they grow. Each resize allocates a new internal array and
  abandons the old one. The old arrays become Gen0 garbage. BinarySplitChunk
  is called ~137,000 times, so these resize allocations occur millions of
  times per billion-digit computation.

  Additionally, maxDepth was already computed at the top of the function
  using Log(b-a, 2) × 2 but was not being used to pre-size the collections.

Fix:
  - workStack is now pre-sized to (maxDepth + 4).
  - results is now pre-sized to CInt(b - a), the number of terms in the
    chunk, which is an upper bound on the number of simultaneous entries.

  Both collections can now be allocated once at their final capacity and will
  not resize during processing of a chunk.

  Also added: a Try/Catch inside BinarySplitChunk that logs the exact WorkItem
  (a, b, isComplete) that was active when a GMP exception occurred, then
  re-throws. This gives precise crash location within any individual chunk.

--------------------------------------------------------------------------------
4.6  Tuple(Of mpz_t, mpz_t, mpz_t) throw-away allocations  [MEDIUM]
--------------------------------------------------------------------------------

Original code (examples):
  SerializeNodeToDisk(Tuple.Create(tempP, tempQ, tempT), node.FilePath)
  node.MemoryNode = Tuple.Create(newP, newQ, newT)
  nodes As List(Of Tuple(Of mpz_t, mpz_t, mpz_t))

Problem:
  Tuple.Create allocates a new heap object for every call. These objects are
  used only to pass three values to a method and are discarded immediately.
  In the chunk loop alone (~137,000 iterations), this creates 137,000 Tuple
  objects that exist for microseconds. In the combine loops at each level,
  further Tuple objects are created. All of these contribute to Gen0/Gen1
  allocation pressure.

Fix:
  - SerializeNodeToDisk signature changed to take three mpz_t parameters
    directly (p, q, t) instead of a Tuple.
  - LoadNodeFromDisk signature changed to return via three ByRef parameters
    instead of returning a Tuple.
  - DiskNode.MemoryNode (Tuple field) replaced with MemP, MemQ, MemT fields
    (enabled by the Class→Structure change in 4.4).
  - BinarySplitGMP return type changed from List(Of Tuple(Of mpz_t,mpz_t,
    mpz_t)) to List(Of Result), using the existing Result Structure that
    already had P, Q, T fields.
  - ComputePiGMP's internal nodes list changed to List(Of Result) throughout.

  Result: all Tuple(Of mpz_t,mpz_t,mpz_t) references removed from the
  codebase. The existing Result Structure is used everywhere instead.

--------------------------------------------------------------------------------
4.7  GC.Collect every 10 combine pairs  [MEDIUM]
--------------------------------------------------------------------------------

Original code:
  If useDisk AndAlso nextDiskNodes.Count Mod 10 = 0 Then
      GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, True, True)
      GC.WaitForPendingFinalizers()
  End If

Problem:
  Aggressive GC collections every 10 node pairs means thousands of
  compaction attempts per level for large computations. This is more
  harmful than helpful once the root causes of fragmentation are addressed:

  1. Compacting GC moves objects in memory to close gaps — but any pinned
     object blocks compaction of its entire heap segment (see 4.1). With
     the pinning fixed, this concern is eliminated, but the cost of the
     frequent GC calls remained.

  2. The GC is designed to make its own collection decisions based on
     allocation rates, generation sizes, and available memory. Forcing it
     every 10 pairs overrides this and can cause collections at suboptimal
     times — for example, immediately after a large mpz_t allocation when
     the GC had not yet decided to collect.

Fix:
  Removed the every-10-pairs GC.Collect call. The between-level GC.Collect
  is retained — it fires approximately 17 times for a 1 billion digit
  computation and provides a reasonable checkpoint to return memory after
  each full tree level completes.

--------------------------------------------------------------------------------
4.8  pi result string (displayStr) held as 2 GB LOH object  [LOW]
--------------------------------------------------------------------------------

Original code:
  displayStr = piString   ' assigned in StreamPiToScreen
  ' displayStr was never set to Nothing afterward

Problem:
  For 1 billion digits, the pi string is approximately 2 GB as a .NET
  UTF-16 String. This is allocated on the LOH. The LOH is not compacted
  by default, so even after the string is no longer needed, its slot in
  the LOH address space is not reusable by other large allocations. If
  the user runs a second computation, the new string cannot occupy the
  same space, and the process requires 4 GB of LOH space for two overlapping
  allocations.

  Additionally, displayStr was declared as a class field, so the GC
  considered it a root — the string could not be collected until something
  else was assigned to the field.

Fix:
  After the display timer fires its final tick (all digits displayed) and
  the optional file write completes, displayStr is set to Nothing:

      displayStr = Nothing
      WriteToLog("[DisplayTimer] displayStr released (LOH block freed)")

  Setting the field to Nothing removes the GC root, allowing the 2 GB
  string to be collected at the next Gen2 sweep.


================================================================================
SECTION 5 — SUMMARY OF ALL MODIFIED CODE LOCATIONS
================================================================================

ApplicationEvents.vb
  - MyApplication_UnhandledException: implemented (was empty stub)
  - Now writes full exception chain to log file before showing dialog

Form1.vb — class-level declarations
  - DiskNode: changed from Class to Structure
  - DiskNode.MemoryNode: replaced with MemP, MemQ, MemT separate fields
  - _nativeCrashCallback: new field (NativeCrashFilterCallback delegate)
  - SetUnhandledExceptionFilter: new P/Invoke declaration
  - NativeCrashFilterCallback: new Delegate type
  - HandleNativeCrash: new crash callback method

Form1.vb — Form1_Load
  - Added AppDomain.CurrentDomain.UnhandledException subscription
  - Added SetUnhandledExceptionFilter registration

Form1.vb — new methods added
  - OnAppDomainUnhandledException
  - WriteToLog
  - WriteExceptionToLog
  - SerializeOneMpz  (extracted helper for SerializeNodeToDisk)
  - DeserializeOneMpz (extracted helper for LoadNodeFromDisk)

Form1.vb — BtnCompute_Click
  - All three catch blocks now call WriteExceptionToLog before MessageBox
  - Log file header now includes the DETAILED_LOGGING mode line

Form1.vb — BinarySplitChunk
  - Stack and Dictionary are now pre-sized
  - Entry/exit WriteToLog calls added (conditional on DETAILED_LOGGING)
  - Try/Catch added around the work loop; logs failing WorkItem on exception

Form1.vb — SerializeNodeToDisk
  - Signature changed: takes p, q, t As mpz_t instead of Tuple
  - Implementation replaced: uses 64 KB staging buffer via SerializeOneMpz
  - WriteToLog calls made conditional on DETAILED_LOGGING
  - FileInfo stat moved inside DETAILED_LOGGING block

Form1.vb — LoadNodeFromDisk
  - Signature changed: returns via ByRef p, q, t instead of Tuple return
  - Implementation replaced: uses Marshal.AllocHGlobal + 64 KB staging buffer
    via DeserializeOneMpz; no pinned managed arrays
  - WriteToLog call made conditional on DETAILED_LOGGING
  - FileInfo stat moved inside DETAILED_LOGGING block

Form1.vb — BinarySplitGMP
  - Return type changed to List(Of Result); all Tuple references removed
  - DiskNode usage updated for MemP/MemQ/MemT fields (struct access)
  - SerializeNodeToDisk calls updated for new 3-parameter signature
  - LoadNodeFromDisk calls updated for ByRef output signature
  - GC.Collect every 10 pairs removed; between-level GC.Collect retained
  - Chunk progress logging frequency increased from every 1,000 to every 100

Form1.vb — ComputePiGMP
  - nodes variable changed from List(Of Tuple) to List(Of Result)
  - Final combine loop uses Result struct throughout
  - All [ComputePi] mpz_xxx WriteToLog calls made conditional on
    DETAILED_LOGGING
  - Node P/Q/T size logging block made conditional on DETAILED_LOGGING
  - Exception catch block now calls WriteExceptionToLog

Form1.vb — DisplayTimer_Tick
  - displayStr = Nothing added after streaming and optional file write complete

Form1.vb — file header
  - #Const DETAILED_LOGGING = True added with explanatory comment block


================================================================================
SECTION 5 (CONTINUED) — CRASH DIAGNOSIS FROM LIVE LOG DATA
================================================================================

The following crash was diagnosed from the log file produced during an earlier
run with DETAILED_LOGGING = True, leading to the fixes in Section 6.

Log excerpt (last entries before silent crash):

  18:04:05 | RAM:   79MB | [PHASE] Level 16: Processing 3 → 2 nodes (DISK mode)
  18:04:05 | RAM:   79MB | [Deserialize] Loading L15_N0.bin  size=646,648KB
  18:04:06 | RAM:1,342MB | [Deserialize] Loading L15_N1.bin  size=683,512KB
  18:04:52 | RAM:2,678MB | [Serialize]   Writing L16_N0.bin
  18:04:56 | RAM:3,977MB | [Serialize]   Done    L16_N0.bin  size=1,330,161KB
  18:04:56 | RAM:   80MB | [PHASE] Combine level 16: 2 nodes remaining
  18:04:56 | RAM:   80MB | [PHASE] Level 17: Processing 2 → 1 nodes (MEMORY mode)
  18:04:56 | RAM:   80MB | [Deserialize] Loading L16_N0.bin  size=1,330,161KB
  18:04:58 | RAM:2,678MB | [Deserialize] Loading L12_N16.bin size=70,421KB
  <process terminates — no further entries>

Analysis
--------

1. Crash location
   The [Deserialize] log entry is written at the START of LoadNodeFromDisk,
   before any reading or GMP allocation occurs. L12_N16.bin (70 MB) is small
   and loaded successfully — the RAM at that point was only ~2,760 MB. The
   crash occurred during the GMP multiplication operations that followed,
   not during the load itself.

2. Why L12_N16.bin appears in Level 17
   Level 16 started with 3 nodes (odd count). Two were combined into
   L16_N0.bin; the third — L12_N16.bin — was carried forward unchanged by
   the odd-node path:

       If diskNodes.Count Mod 2 = 1 Then
           nextDiskNodes.Add(diskNodes(diskNodes.Count - 1))
       End If

   L12_N16.bin had been carried forward untouched since Level 12, through
   Levels 13, 14, 15, and 16. This is mathematically correct (binary
   splitting is associative), but it meant the final Level 17 combine was
   severely unbalanced:

       Left  (L16_N0.bin):  1,330,161 KB  (~1.27 GB on disk)
       Right (L12_N16.bin):    70,421 KB  (~68 MB on disk)

3. Peak memory calculation — why the combine crashed
   The combine formula requires 4 multiplications and 1 addition:

       newP  = leftP  × rightP
       newQ  = leftQ  × rightQ
       tempA = leftT  × rightQ
       tempB = leftP  × rightT      ← leftP reused here
       newT  = tempA  + tempB

   The original code freed all 6 input operands only after all 5 operations
   completed. At the point where tempB was being allocated, alive in memory
   were all 6 inputs plus newP, newQ, tempA, and tempB being constructed.
   Estimated peak for Level 17:

       6 inputs             ~2,760 MB  (both loaded nodes)
       + newP               ~  650 MB
       + newQ               ~  450 MB
       + tempA              ~  650 MB
       + tempB (allocating) ~  650 MB
                           ──────────
       Peak                ~5,160 MB

   Level 16 had peaked at 3,977 MB (visible in the log) and the GC
   successfully freed back to 80 MB afterward. Level 17's peak was
   approximately 1.2 GB higher, exceeding available committed memory.

4. DETAILED_LOGGING = True was essential for diagnosing this
   Without per-file serialize/deserialize logging, the last log entry would
   have been the "[PHASE] Level 17: Processing 2 → 1 nodes" line, giving
   no indication of which specific node was being loaded or what the memory
   state was. The detailed log identified the exact operand sizes and the
   RAM at each step, making the peak-memory calculation possible.


================================================================================
SECTION 6 — GMP OPERAND EARLY-FREE OPTIMISATION
================================================================================

Problem
-------
Identified by crash diagnosis in Section 5 (continued).

In both combine loops — the disk-mode loop in BinarySplitGMP and the final
in-memory loop in ComputePiGMP — all six input operands (leftP, leftQ, leftT,
rightP, rightQ, rightT) were kept alive until after all four GMP multiplications
and one addition completed, then freed together:

    gmp_lib.mpz_mul(newP,  leftP,  rightP)
    gmp_lib.mpz_mul(newQ,  leftQ,  rightQ)
    gmp_lib.mpz_mul(tempA, leftT,  rightQ)
    gmp_lib.mpz_mul(tempB, leftP,  rightT)
    gmp_lib.mpz_add(newT,  tempA,  tempB)

    gmp_lib.mpz_clears(tempA, tempB, Nothing)
    gmp_lib.mpz_clears(leftP, leftQ, leftT, Nothing)   ← freed too late
    gmp_lib.mpz_clears(rightP, rightQ, rightT, Nothing)

At peak (when tempB was being allocated), ten large GMP integers were live
simultaneously — all 6 inputs plus newP, newQ, tempA, and the tempB result
being written. This inflated peak memory by approximately 2 GB above the
minimum required.

Fix — dependency-ordered early freeing
---------------------------------------
Each operand is now freed immediately after its last use. The dependency
map determines the earliest safe point for each:

    Operand   Last used in    Free after
    ────────  ──────────────  ──────────────────────────────
    rightP    newP   = leftP  × rightP     → free after step 1
    leftQ     newQ   = leftQ  × rightQ     → free after step 2
    rightQ    tempA  = leftT  × rightQ     → free after step 3  (also used in step 2)
    leftT     tempA  = leftT  × rightQ     → free after step 3
    leftP     tempB  = leftP  × rightT     → free after step 4  (also used in step 1)
    rightT    tempB  = leftP  × rightT     → free after step 4

New code (both BinarySplitGMP and ComputePiGMP combine loops):

    gmp_lib.mpz_mul(newP,  leftP,  rightP)
    gmp_lib.mpz_clears(rightP, Nothing)              ' rightP done

    gmp_lib.mpz_mul(newQ,  leftQ,  rightQ)
    gmp_lib.mpz_clears(leftQ, Nothing)               ' leftQ done

    gmp_lib.mpz_mul(tempA, leftT,  rightQ)
    gmp_lib.mpz_clears(leftT, rightQ, Nothing)       ' leftT, rightQ done

    gmp_lib.mpz_mul(tempB, leftP,  rightT)
    gmp_lib.mpz_clears(leftP, rightT, Nothing)       ' leftP, rightT done

    gmp_lib.mpz_add(newT, tempA, tempB)
    gmp_lib.mpz_clears(tempA, tempB, Nothing)

Peak memory with this change — at the point tempB is being allocated:

    leftP + rightT              ~  673 MB  (freed immediately after)
    + newP + newQ + tempA       ~1,750 MB  (results of steps 1–3)
    + tempB (allocating)        ~  650 MB
                               ──────────
    Peak                       ~3,073 MB

Reduction: approximately 2,090 MB (~2 GB) lower peak than before.
This is the difference between the Level 17 combine crashing and completing.

Affected locations:
  - BinarySplitGMP: the disk-mode combine loop (runs at every tree level)
  - ComputePiGMP: the final in-memory combine loop

Note: BinarySplitChunk's internal combine loop (the isComplete branch) is
not affected because the operands there are always small (single-term or
sub-chunk results), so peak memory is not a concern.


================================================================================
SECTION 7 — DETAILED_LOGGING DEFAULT CHANGED TO FALSE
================================================================================

After the Level 17 crash was diagnosed and fixed (Section 6), DETAILED_LOGGING
was set to False for normal production runs:

    #Const DETAILED_LOGGING = False   (was True during crash investigation)

Rationale:
  With the root cause identified and the peak-memory fix in place, the
  high-volume per-operation trace is no longer needed for every run.
  Disabling it removes ~550,000+ file writes per billion-digit computation,
  reducing log I/O overhead and keeping the log file focused on phase
  transitions and any exceptions that occur.

  DETAILED_LOGGING = True should be re-enabled if new crash symptoms appear
  that are not explained by the major-phase log alone.


================================================================================
SECTION 8 — GRANULAR CRASH-LOCATION LOGGING
================================================================================

Problem
-------
After adding the early-free optimisation (Section 6), the Level 17 combine
continued to crash. The existing per-file logging (LoadNodeFromDisk "Loading")
identified which node was being read but gave no indication of whether the crash
occurred inside the load itself or in the GMP multiply operations that followed.
Similarly, when BinarySplitGMP runs the combine steps, there was no way to
identify which of the five GMP operations (mul newP, mul newQ, mul tempA,
mul tempB, add newT) was the one that caused the process to abort.

Changes
-------

8.1  LoadNodeFromDisk — "[Deserialize] Done" completion entry

  Added a WriteToLog call at the end of LoadNodeFromDisk, after the Using
  blocks have closed successfully:

      WriteToLog($"[Deserialize] Done {filename}")

  Why: The "Loading" entry is written at the very start of the function,
  before any file I/O or GMP allocation. If the crash happens inside the
  function (e.g. during mpz_import in DeserializeOneMpz), the "Done" entry
  never appears. Its absence definitively places the crash inside
  LoadNodeFromDisk rather than in code that follows it.

8.2  BinarySplitGMP combine loop — per-GMP-operation step markers

  Added WriteToLog calls immediately before each of the five GMP operations
  in the combine loop, and one immediately after mpz_add:

      [Combine] L{level} N{pair}: mul newP
      [Combine] L{level} N{pair}: mul newQ
      [Combine] L{level} N{pair}: mul tempA
      [Combine] L{level} N{pair}: mul tempB
      [Combine] L{level} N{pair}: add newT (in-place into tempA)
      [Combine] L{level} N{pair}: combine complete

  Why: Each entry is written to disk before the GMP call it labels. When the
  process is killed by GMP's abort(), the last visible entry in the log is
  the one immediately before the failing call. This reduced a list of five
  suspects to a single identified call.

  Diagnostic result: log analysis showed the process died during
  mpz_add(newT, tempA, tempB). Since mpz_add is expected to be far cheaper
  than the multiplications (which had all succeeded), this pointed to a
  memory allocation failure during the allocation of the fresh newT buffer —
  confirmed by the peak-memory analysis in Section 9.


================================================================================
SECTION 9 — IN-PLACE mpz_add OPTIMISATION (Level 17 second crash fix)
================================================================================

Problem
-------
After the early-free fix (Section 6), the Level 17 combine reached the
mpz_add step successfully but then crashed. Per-step logging (Section 8)
confirmed the failure was in mpz_add(newT, tempA, tempB).

Root cause
----------
At the point of the add, newT is still an uninitialised (zero-value) mpz_t
with no limb buffer allocated. GMP must allocate a fresh buffer of
approximately the same size as tempA (~443 MB) to hold the result. At that
moment the following are simultaneously live in GMP's heap:

    newP   (~443 MB)   — result of step 1
    newQ   (~443 MB)   — result of step 2
    tempA  (~443 MB)   — result of step 3
    tempB  (~443 MB)   — result of step 4
    newT   (allocating ~443 MB)

    Peak: ~2,215 MB

This allocation pushed the process over the available memory ceiling, causing
GMP's internal malloc to fail. GMP responds to a failed malloc by calling
abort(), which terminates the process without raising a .NET exception —
bypassing all managed and SetUnhandledExceptionFilter handlers.

Fix — alias mpz_add to reuse tempA's existing buffer
-----------------------------------------------------
GMP §5.5 explicitly permits the destination to alias one of the source
operands. Instead of allocating a fresh result variable:

    Old:  gmp_lib.mpz_add(newT, tempA, tempB)   ' allocates ~443 MB for newT
          gmp_lib.mpz_clears(tempA, tempB, Nothing)

    New:  gmp_lib.mpz_add(tempA, tempA, tempB)  ' extends tempA by at most 1 limb
          gmp_lib.mpz_clears(tempB, Nothing)     ' tempA IS the T result now

Because tempA already has a ~443 MB limb buffer, the in-place add only needs
to extend it by at most one additional limb (8 bytes) for a potential carry.
The fresh ~443 MB allocation is avoided entirely. The newT variable is
removed; tempA is used directly as the T component of the result node.

Peak memory with this fix at the add step:

    newP + newQ + tempA (extended) + tempB  ≈  1,772 MB

Reduction: ~443 MB lower than before. The add now completes successfully.

Affected locations:
  - BinarySplitGMP: disk-mode combine loop
  - ComputePiGMP: final in-memory combine loop
  The newT Dim declaration and its mpz_inits entry were removed from both.


================================================================================
SECTION 10 — ComputePiGMP MEMORY OPTIMISATIONS (final-phase crash fixes)
================================================================================

Problem
-------
After the Level 17 combine completed successfully (Sections 9), the process
reached the final ComputePiGMP arithmetic phase and began crashing at the
large multiplication  gmpNumer *= finalQ. Per-step logging traced a sequence
of crashes through several iterations as each fix reduced peak memory further.

Background — memory state before the large multiply
----------------------------------------------------
The final phase computes:

    pi = (426880 * sqrt(10005 * 10^(2d)) * Q) / T

where d = digit count. In code:

    gmpOne       = 10^d                   (~198 MB for 500M digits)
    gmpSqrtInput = gmpOne^2 * 10005       (~396 MB)
    gmpSqrt      = isqrt(gmpSqrtInput)    (~198 MB; gmpSqrtInput freed)
    gmpNumer     = gmpSqrt * 426880       (~198 MB)
    gmpNumer     = gmpNumer * finalQ      ← LARGE MULTIPLY
    gmpPi        = gmpNumer / finalT

At the start of the large multiply, the objects that were alive without any
optimisation:

    finalP     (~340 MB)  — P from binary split; NOT used in final formula
    finalQ     (~548 MB)  — operand for the multiply
    finalT     (~548 MB)  — operand for the division that follows
    gmpSqrt    (~198 MB)  — value now encoded in gmpNumer; not needed
    gmpNumer   (~198 MB)  — being multiplied; becomes ~754 MB result
    gmpOne     (~198 MB)  — used only for gmpSqrtInput; not needed since then

    Baseline:  ~2,030 MB
    After multiply result (~754 MB) + GMP FFT scratch (~238 MB): ~3,022 MB

Four fixes were applied in sequence, each confirmed by log analysis:

10.1  gmpOne freed after gmpSqrtInput = gmpOne^2

  gmpOne is only needed for the one squaring operation. After
  mpz_mul(gmpSqrtInput, gmpOne, gmpOne) completes, gmpOne's value is no
  longer referenced. It is now explicitly freed with mpz_clear immediately
  after that line, then re-initialised to 0 with mpz_init so that the
  Finally block's mpz_clear(gmpOne) call remains safe.

  Saving: ~198 MB off the baseline for all subsequent operations.

10.2  gmpSqrt freed after gmpNumer = gmpSqrt * 426880

  After mpz_mul_ui(gmpNumer, gmpSqrt, 426880) completes, gmpSqrt's value
  is encoded in gmpNumer and gmpSqrt is no longer used. It is freed with
  mpz_clears(gmpSqrt, ...) before the large multiply, not after it.

  Saving: ~198 MB off the baseline before the large multiply.

10.3  finalP freed before the large multiply

  finalP is the P component from the binary splitting. In the Chudnovsky
  final formula (pi = 426880 * sqrt(10005) * Q / T), only Q and T appear;
  P accumulates intermediate sub-products during splitting and is not used
  in the final division. finalP is freed before the large multiply and
  removed from the mpz_clears call at the end of the function.

  Saving: ~340 MB off the baseline before the large multiply.

10.4  finalT spilled to disk before the large multiply, reloaded after

  Even after the three frees above, the large multiply still crashed because
  finalT (~548 MB) remained alive solely because it was needed for the
  division that comes AFTER the multiply. By spilling finalT to a temporary
  file before the multiply and reloading it afterward, its 548 MB is freed
  during the multiply's peak.

  Implementation:
    - Before the multiply: write finalT to DISK_CACHE_DIR\finalT_spill.bin
      using the existing SerializeOneMpz helper; then mpz_clear(finalT).
    - After the multiply and after finalQ is freed:
      mpz_init(finalT); read back via DeserializeOneMpz; delete temp file.
    - The spill file is in the same NodeCache directory as other cache files.

  Saving: ~548 MB off the baseline before the large multiply.

Combined memory state before the large multiply after all four fixes:

    finalQ     (~548 MB)  — operand
    gmpNumer   (~198 MB)  — operand
    gmpOne     (trivial)  — freed and re-inited to 0
                          — gmpSqrt, finalP, finalT all freed
    Baseline:  ~750 MB

    After multiply result (~754 MB) + FFT scratch (~238 MB): ~1,742 MB

This is comfortably below the ~1,843 MB the system demonstrated it could
handle during the sqrt operation.


================================================================================
SECTION 11 — THREE-TIER LOGGING_DETAIL CONSTANT
================================================================================

Problem
-------
The boolean #Const DETAILED_LOGGING provided two modes: full detail on every
operation at every level (True), or major phases only (False). For crash
investigation at the final levels of a billion-digit run, full detail is
essential — but enabling it for the entire run adds significant I/O overhead
because the early levels process tens of thousands of node pairs.

For a 1-billion-digit computation:
  - ~137,000 BinarySplitChunk calls, each generating 2 log entries
  - ~137,000 serialize calls generating 2 entries each
  - Thousands of deserialize calls generating 2 entries each
  - 6 entries per combine pair at every level across ~17 levels

With DETAILED_LOGGING = True, this amounts to over 1 million file writes
before the final level is even reached. At ~100 µs per AppendAllText call,
that is over 100 seconds of pure log I/O — a significant fraction of the
total runtime.

Changes
-------
Replaced #Const DETAILED_LOGGING (boolean) with #Const LOGGING_DETAIL
(integer, three values):

    LOGGING_DETAIL = 0   Major [PHASE] markers and exceptions only.
                         Zero per-operation I/O overhead.
                         Use for normal stable runs.

    LOGGING_DETAIL = 1   Detail on the LAST COMBINE LEVEL only, plus all
                         ComputePiGMP steps.  (Default / recommended)
                         The final-level and final-computation steps are the
                         most critical for crash diagnosis. Logging them adds
                         at most ~50 file writes per run regardless of the
                         digit count.

    LOGGING_DETAIL = 2   Full trace: every BinarySplitChunk call and every
                         combine level at every node pair. Use only when
                         debugging a crash in an early level.

Implementation details
----------------------
Runtime gate — isLastLevel:
  A Boolean local variable isLastLevel = (currentSize <= 2) is computed once
  at the start of each outer combine-level iteration inside BinarySplitGMP.
  When currentSize = 2, the loop is processing the final pair (the level that
  reduces from 2 nodes to 1). This is true only once per run regardless of
  digit count.

Optional detailLog parameter:
  SerializeNodeToDisk and LoadNodeFromDisk now accept an Optional
  detailLog As Boolean = True parameter. BinarySplitGMP's combine loop
  passes isLastLevel; the Phase 3 final-node load uses the default True
  (always logs the final load regardless of LOGGING_DETAIL).

Conditional compilation pattern:
  - BinarySplitChunk enter/exit:
      #If LOGGING_DETAIL = 2 Then ... #End If

  - SerializeNodeToDisk, LoadNodeFromDisk, Combine loop per-step:
      #If LOGGING_DETAIL = 2 Then
          WriteToLog(...)
      #ElseIf LOGGING_DETAIL = 1 Then
          If detailLog/isLastLevel Then WriteToLog(...)
      #End If

  - All ComputePiGMP steps:
      #If LOGGING_DETAIL >= 1 Then ... #End If

The old DETAILED_LOGGING constant is removed entirely from the codebase.


================================================================================
SECTION 12 — COMPLETE UPDATED SUMMARY OF ALL MODIFIED CODE LOCATIONS
================================================================================

ApplicationEvents.vb
  - MyApplication_UnhandledException: implemented (was empty stub)
  - Writes full exception chain to log file before showing dialog

Form1.vb — file header
  - Removed #Const DETAILED_LOGGING
  - Added #Const LOGGING_DETAIL = 1 with three-level description (Section 11)

Form1.vb — class-level declarations
  - DiskNode: changed from Class to Structure
  - DiskNode.MemoryNode: replaced with MemP, MemQ, MemT separate fields
  - _nativeCrashCallback: new field (NativeCrashFilterCallback delegate)
  - SetUnhandledExceptionFilter: new P/Invoke declaration
  - NativeCrashFilterCallback: new Delegate type
  - HandleNativeCrash: new crash callback method

Form1.vb — Form1_Load
  - Added AppDomain.CurrentDomain.UnhandledException subscription
  - Added SetUnhandledExceptionFilter registration

Form1.vb — new methods added
  - OnAppDomainUnhandledException
  - WriteToLog
  - WriteExceptionToLog
  - SerializeOneMpz  (extracted helper for SerializeNodeToDisk)
  - DeserializeOneMpz (extracted helper for LoadNodeFromDisk)

Form1.vb — BtnCompute_Click
  - All catch blocks now call WriteExceptionToLog before MessageBox
  - Log file header includes the LOGGING_DETAIL mode description

Form1.vb — BinarySplitChunk
  - Stack and Dictionary are now pre-sized
  - Entry/exit WriteToLog calls present (LOGGING_DETAIL = 2 only)
  - Try/Catch logs failing WorkItem on exception

Form1.vb — SerializeNodeToDisk
  - Signature: takes p, q, t As mpz_t + Optional detailLog As Boolean = True
  - Implementation: 64 KB SOH staging buffer via SerializeOneMpz (no LOH)
  - Logging gated on LOGGING_DETAIL level and detailLog parameter

Form1.vb — LoadNodeFromDisk
  - Signature: ByRef p, q, t + Optional detailLog As Boolean = True
  - Implementation: Marshal.AllocHGlobal + 64 KB staging (no pinned arrays)
  - "Loading" entry written at start; "Done" entry written on success
  - Logging gated on LOGGING_DETAIL level and detailLog parameter

Form1.vb — BinarySplitGMP (disk-mode combine loop)
  - Return type: List(Of Result); all Tuple references removed
  - DiskNode struct fields MemP/MemQ/MemT used throughout
  - isLastLevel = (currentSize <= 2) set at start of each level iteration
  - SerializeNodeToDisk and LoadNodeFromDisk called with isLastLevel
  - GC.Collect every 10 pairs removed; between-level GC.Collect retained
  - Chunk progress logging every 100 (was 1,000)
  - Early-free: each input operand freed after its last multiply (Section 6)
  - In-place add: mpz_add(tempA, tempA, tempB) replaces separate newT alloc
    (Section 9); newT variable removed; tempA used directly as T result

Form1.vb — ComputePiGMP (final arithmetic)
  - nodes: List(Of Result) throughout; all Tuple references removed
  - Final combine loop: same early-free and in-place-add pattern as above
  - gmpOne freed (mpz_clear + mpz_init) immediately after gmpSqrtInput
    = gmpOne^2; saves ~198 MB during sqrt and subsequent operations
  - gmpSqrt and finalP freed together before gmpNumer *= finalQ;
    saves ~538 MB off the multiply baseline
  - finalT spilled to DISK_CACHE_DIR\finalT_spill.bin before the multiply,
    reloaded with mpz_init + DeserializeOneMpz after; saves ~548 MB
  - mpz_clears at end of function updated: finalP and gmpSqrt no longer
    listed (already freed); gmpOne cleared via Finally (re-inited to 0)
  - All [ComputePi] WriteToLog calls gated on LOGGING_DETAIL >= 1
  - Node P/Q/T size logging gated on LOGGING_DETAIL >= 1
  - Exception catch block calls WriteExceptionToLog

Form1.vb — DisplayTimer_Tick
  - displayStr = Nothing after streaming completes (releases ~2 GB LOH block)


================================================================================
SECTION 13 — SPLIT MULTIPLY ATTEMPTS (gmpNumer *= finalQ)
================================================================================

Problem
-------
After all four ComputePiGMP memory optimisations (Section 10), the large
multiply  gmpNumer *= finalQ  continued to crash intermittently. The crash was
not in the first attempt to run the multiply — it was observed only on certain
runs, and the log showed the baseline memory before the multiply was well within
the range that had previously succeeded.

Background — committed memory vs working set
--------------------------------------------
Two distinct memory metrics determine whether an allocation succeeds:

  Working set (RAM physically in use):
    Pages currently mapped to physical RAM. The Task Manager "Memory" column.
    Can be large or small depending on recent access patterns.

  Committed memory (virtual address space backed by RAM or page file):
    Pages reserved such that the OS guarantees they can be accessed.
    Windows enforces a system-wide commit limit (physical RAM + page file size).
    VirtualAlloc and malloc both consume commit charge. The commit limit
    is typically ~2 GB on a system with 2 GB RAM and a small page file.

The critical distinction: freed memory does NOT reduce commit charge unless
the allocator explicitly calls VirtualFree (or equivalent). The CRT's malloc/
free manages a pool — it calls VirtualAlloc to grow the pool but returns freed
blocks to an internal free-list rather than calling VirtualFree to decommit
them. Pages returned to the free-list remain committed.

GMP is a native C library linked against the static CRT. All GMP allocations
(limb buffers, FFT scratch arrays) go through the CRT malloc/free inside
libgmp-10.dll. When GMP frees a large limb buffer, the pages stay committed
in the CRT heap's free-list.

Consequence: each pass of a multi-pass multiply allocates and frees hundreds
of MB of GMP scratch and result buffers. The commit charge after each pass is
HIGHER than before it, even though the working set appears low. Over multiple
passes plus the Level 17 combine, sqrt, and split operations, the accumulated
committed-but-free pages fill the system commit limit before the final pass
can allocate its result buffer. GMP's internal malloc then fails and calls
abort() — bypassing all .NET exception handlers.

This explained the paradox observed in the logs: Pass B/2 of a split multiply
would crash at a LOWER baseline working set than the pass that succeeded,
because committed memory (which is not shown by the working set log entries)
had accumulated to the limit during the earlier passes.

Attempt — 2-way split multiply
--------------------------------
gmpNumer *= finalQ was split into:
  Pass A:  mpR_A = gmpNumer * Q_low    (low half of finalQ)
  Pass B:  gmpNumer = gmpNumer * Q_high (high half, in-place)
  Combine: gmpNumer = (gmpNumer << halfBits) + mpR_A

Log result:
  Pass A: baseline RAM ~278 MB → succeeded
  Pass B: baseline RAM ~539 MB → crashed (commit charge had accumulated)

Attempt — 3-way split multiply
--------------------------------
finalQ was split into three equal thirds (Q0, Q1, Q2 each ~1/3 of totalBits):
  Pass 0:  mpR0 = gmpNumer * Q0
  Pass 1:  mpR1 = gmpNumer * Q1
  Pass 2:  gmpNumer = gmpNumer * Q2 (in-place)
  Combine: result = ((gmpNumer << k) + mpR1) << k + mpR0

where k = thirdBits (one third of finalQ's bit length).

This reduced each sub-multiply's peak (scratch + result) but did not solve
the root problem — committed memory still accumulated across passes.

Log result:
  Pass 0: baseline RAM ~276 MB → succeeded
  Pass 1: baseline RAM ~277 MB → succeeded
  Pass 2: baseline RAM ~451 MB → crashed

The paradox was identical: Pass 2 failed at a LOWER working-set baseline than
Pass 0 and Pass 1 had succeeded at, because commit charge had accumulated
across the earlier passes and the Level 17 combine before them.

Conclusion: splitting the multiply reduces per-pass peak working set but does
not reduce committed memory — which is the actual resource being exhausted.
The correct fix requires GMP to call VirtualFree when freeing large blocks so
that committed memory is immediately reclaimed.


================================================================================
SECTION 14 — CUSTOM GMP ALLOCATOR (VirtualAlloc/VirtualFree)
================================================================================

Problem
-------
As diagnosed in Section 13, the root cause of the abort() crashes was committed
memory accumulation in GMP's static CRT heap. The CRT free() returns freed
large blocks to the heap free-list without calling VirtualFree, so pages remain
committed even after GMP "frees" a limb buffer. Over a long computation the
accumulated committed-but-free pages fill the system commit limit.

Fix — mp_set_memory_functions with VirtualAlloc/VirtualFree
------------------------------------------------------------
GMP provides the mp_set_memory_functions API (§14.6 of the GMP manual) to
replace the three allocator functions used for all GMP internal allocations:

    allocate(size)                → returns new pointer
    reallocate(ptr, oldSize, newSize) → returns new pointer
    free(ptr, size)               → void

By installing custom functions, every GMP limb buffer and FFT scratch
allocation can be routed through VirtualAlloc, and every free through
VirtualFree. VirtualFree with MEM_RELEASE immediately decommits the pages —
there is no free-list and no commit-charge accumulation.

Implementation — threshold-based routing
-----------------------------------------
VirtualAlloc has a minimum granularity of 64 KB and non-trivial overhead per
call. GMP makes many small allocations for metadata and small integers. Routing
all allocations through VirtualAlloc would be wasteful. A threshold is used:

    GMP_LARGE_THRESHOLD = 512 KB (524288 bytes)

    size < threshold  → Marshal.AllocHGlobal / FreeHGlobal
                        (process default heap; efficient for small blocks)

    size >= threshold → VirtualAlloc(MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE)
                      / VirtualFree(MEM_RELEASE)
                        (immediate decommit guaranteed for large limb buffers)

The realloc function handles all four combinations of old/new size crossing
the threshold: small→small, large→large, small→large, large→small. The
large→large case uses VirtualAlloc(new) + CopyMemory + VirtualFree(old)
rather than in-place realloc (VirtualAlloc does not support in-place growth).

Delegate lifetime
-----------------
The three delegate objects (_gmpAlloc, _gmpRealloc, _gmpFree) are stored as
instance fields on Form1. If they were local variables, the GC would collect
them after InitGmpVirtualAllocFunctions returned, leaving GMP holding dangling
function pointers — a crash the first time GMP allocates after collection.

Initialisation
--------------
InitGmpVirtualAllocFunctions() is called in Form1_Load before the dummy mpz_t
init. From that point on, every GMP allocation in the process uses the custom
allocator, including all subsequent binary-splitting and final-computation
operations.

SerializeOneMpz fix
-------------------
After installing custom allocators, SerializeOneMpz was broken: it called
  gmp_lib.mpz_export(void_ptr.Zero, count, ...)
with a NULL destination, causing GMP to allocate the export buffer internally.
After the custom allocator is set, that allocation goes through VirtualAlloc
for large numbers. But gmp_lib.free(nativePtr) calls the CRT free() — the
wrong allocator for a VirtualAlloc'd pointer — causing an immediate crash.

Fix: SerializeOneMpz now pre-allocates a buffer with Marshal.AllocHGlobal
(sized from mpz_sizeinbase) and passes it to mpz_export directly. GMP writes
into the caller's buffer and makes no allocation of its own. The gmp_lib.free
call is eliminated entirely.

Code locations
--------------
Form1.vb — new declarations (after commented-out bump allocator):
  - GMP_LARGE_THRESHOLD As Long = 524288
  - VirtualAlloc, VirtualFree, CopyMemory P/Invoke declarations
  - MEM_COMMIT_RESERVE, MEM_RELEASE, VA_PAGE_READWRITE constants
  - _gmpAlloc, _gmpRealloc, _gmpFree instance fields (delegate GC anchors)
  - GmpAllocFunc, GmpReallocFunc, GmpFreeFunc Shared methods
  - InitGmpVirtualAllocFunctions() method

Form1.vb — Form1_Load:
  - InitGmpVirtualAllocFunctions() called before dummy mpz_t init

Form1.vb — SerializeOneMpz:
  - Pre-allocates Marshal.AllocHGlobal buffer from mpz_sizeinbase
  - Passes pre-allocated buffer to mpz_export (no GMP allocation)
  - gmp_lib.free(nativePtr) removed; Marshal.FreeHGlobal in Finally block

The 3-way split multiply code (added in Section 13 attempts) is retained as
the mechanism used to call the multiply — each pass is now smaller, and with
the custom allocator ensuring immediate decommit, commit charge no longer
accumulates across passes.


================================================================================
SECTION 15 — COMPLETE UPDATED SUMMARY OF ALL MODIFIED CODE LOCATIONS
================================================================================

(Supersedes Section 12)

ApplicationEvents.vb
  - MyApplication_UnhandledException: implemented (was empty stub)
  - Writes full exception chain to log file before showing dialog

Form1.vb — file header
  - Removed #Const DETAILED_LOGGING
  - Added #Const LOGGING_DETAIL = 1 with three-level description (Section 11)

Form1.vb — class-level declarations
  - DiskNode: changed from Class to Structure
  - DiskNode.MemoryNode: replaced with MemP, MemQ, MemT separate fields
  - _nativeCrashCallback: new field (NativeCrashFilterCallback delegate)
  - SetUnhandledExceptionFilter: new P/Invoke declaration
  - NativeCrashFilterCallback: new Delegate type
  - HandleNativeCrash: new crash callback method
  - GMP_LARGE_THRESHOLD constant (512 KB)
  - VirtualAlloc, VirtualFree, CopyMemory P/Invoke declarations
  - MEM_COMMIT_RESERVE, MEM_RELEASE, VA_PAGE_READWRITE constants
  - _gmpAlloc, _gmpRealloc, _gmpFree instance fields (GC anchors for delegates)
  - GmpAllocFunc, GmpReallocFunc, GmpFreeFunc Shared allocator methods
  - InitGmpVirtualAllocFunctions() method

Form1.vb — Form1_Load
  - InitGmpVirtualAllocFunctions() called before dummy mpz_t init
  - AppDomain.CurrentDomain.UnhandledException subscription
  - SetUnhandledExceptionFilter registration

Form1.vb — new methods added
  - OnAppDomainUnhandledException
  - WriteToLog
  - WriteExceptionToLog
  - SerializeOneMpz  (extracted helper for SerializeNodeToDisk)
  - DeserializeOneMpz (extracted helper for LoadNodeFromDisk)

Form1.vb — BtnCompute_Click
  - All catch blocks now call WriteExceptionToLog before MessageBox
  - Log file header includes the LOGGING_DETAIL mode description

Form1.vb — BinarySplitChunk
  - Stack and Dictionary are now pre-sized
  - Entry/exit WriteToLog calls present (LOGGING_DETAIL = 2 only)
  - Try/Catch logs failing WorkItem on exception

Form1.vb — SerializeNodeToDisk
  - Signature: takes p, q, t As mpz_t + Optional detailLog As Boolean = True
  - Implementation: 64 KB SOH staging buffer via SerializeOneMpz (no LOH)
  - Logging gated on LOGGING_DETAIL level and detailLog parameter

Form1.vb — SerializeOneMpz
  - Pre-allocates Marshal.AllocHGlobal buffer from mpz_sizeinbase
  - Passes buffer to mpz_export directly (no GMP-internal allocation)
  - gmp_lib.free removed; Marshal.FreeHGlobal in Finally block

Form1.vb — LoadNodeFromDisk
  - Signature: ByRef p, q, t + Optional detailLog As Boolean = True
  - Implementation: Marshal.AllocHGlobal + 64 KB staging (no pinned arrays)
  - "Loading" entry written at start; "Done" entry written on success
  - Logging gated on LOGGING_DETAIL level and detailLog parameter

Form1.vb — BinarySplitGMP (disk-mode combine loop)
  - Return type: List(Of Result); all Tuple references removed
  - DiskNode struct fields MemP/MemQ/MemT used throughout
  - isLastLevel = (currentSize <= 2) set at start of each level iteration
  - SerializeNodeToDisk and LoadNodeFromDisk called with isLastLevel
  - GC.Collect every 10 pairs removed; between-level GC.Collect retained
  - Chunk progress logging every 100 (was 1,000)
  - Early-free: each input operand freed after its last multiply (Section 6)
  - In-place add: mpz_add(tempA, tempA, tempB) replaces separate newT alloc
    (Section 9); newT variable removed; tempA used directly as T result

Form1.vb — ComputePiGMP (final arithmetic)
  - nodes: List(Of Result) throughout; all Tuple references removed
  - Final combine loop: same early-free and in-place-add pattern as above
  - gmpOne freed (mpz_clear + mpz_init) immediately after gmpSqrtInput
    = gmpOne^2; saves ~198 MB during sqrt and subsequent operations
  - gmpSqrt and finalP freed together before the large multiply;
    saves ~538 MB off the multiply baseline
  - finalT spilled to DISK_CACHE_DIR\finalT_spill.bin before the multiply,
    reloaded with mpz_init + DeserializeOneMpz after; saves ~548 MB
  - gmpNumer *= finalQ implemented as 3-way split multiply (Section 13):
      finalQ split into Q0, Q1, Q2 (equal bit-thirds); Q1 and Q2 spilled
      to disk; three sub-multiplies with combine via shift-and-add
  - mpz_clears at end of function updated accordingly
  - All [ComputePi] WriteToLog calls gated on LOGGING_DETAIL >= 1
  - Node P/Q/T size logging gated on LOGGING_DETAIL >= 1
  - Exception catch block calls WriteExceptionToLog

Form1.vb — DisplayTimer_Tick
  - displayStr = Nothing after streaming completes (releases ~2 GB LOH block)


================================================================================
SECTION 14 CORRECTIONS
================================================================================

Three inaccuracies in the original Section 14 description versus the actual code:

Correction 14-A — Small allocation routing
------------------------------------------
Section 14 stated:
  size < threshold → Marshal.AllocHGlobal / FreeHGlobal
                     (process default heap; efficient for small blocks)

Actual code:
  Small allocations are delegated to GMP's own saved CRT allocator
  (_savedGmpAlloc), which is the static CRT heap inside libgmp-10.dll.
  Marshal.AllocHGlobal would allocate from the process default heap — a
  different heap from the one GMP's small-block bookkeeping data lives on.
  Mixing the two heaps for the same struct bodies corrupts GMP's internal
  state and caused crashes (NullReferenceException in BinarySplitChunk)
  during early testing of the custom allocator.

  The correct routing for GmpReallocFunc is therefore:
    small → small:  _savedGmpRealloc  (unchanged CRT behaviour)
    large → large:  VirtualAlloc(new) + CopyMemory + VirtualFree(old)
    small → large:  VirtualAlloc(new) + CopyMemory + _savedGmpFree(old)
    large → small:  _savedGmpAlloc(new) + CopyMemory + VirtualFree(old)

Correction 14-B — Delegate field storage class
-----------------------------------------------
Section 14 stated:
  "The three delegate objects (_gmpAlloc, _gmpRealloc, _gmpFree) are stored
  as instance fields on Form1."

Actual code:
  All six delegate fields are declared Private Shared:
    Private Shared _gmpAlloc As allocate_function
    Private Shared _gmpRealloc As reallocate_function
    Private Shared _gmpFree As free_function
    Private Shared _savedGmpAlloc As allocate_function
    Private Shared _savedGmpRealloc As reallocate_function
    Private Shared _savedGmpFree As free_function
  (The GC-anchor property is the same whether fields are instance or Shared
  on a single-window application; the Shared declaration is what is in the
  code.)

Correction 14-C — Missing _savedGmpAlloc/Realloc/Free fields
-------------------------------------------------------------
Section 14 and Section 15 listed only _gmpAlloc, _gmpRealloc, _gmpFree.
The code also declares _savedGmpAlloc, _savedGmpRealloc, and _savedGmpFree,
populated in step 1 of InitGmpVirtualAllocFunctions by calling
gmp_lib.mp_get_memory_functions.  These hold GMP's original CRT malloc,
realloc, and free function pointers, which the custom allocator delegates
to for all small (<512 KB) allocations and frees.

Correction 14-D — GmpSetMemoryFunctionsNative P/Invoke not in Section 15
-------------------------------------------------------------------------
The body text of Section 14 described the direct P/Invoke to
__gmp_set_memory_functions in libgmp-10.dll and explained why
Math.Gmp.Native's mp_set_memory_functions wrapper could not be used
(it re-reads the table via _get_memory_functions and the subsequent
Marshal.GetDelegateForFunctionPointer cast fails under .NET 10).
This declaration was omitted from Section 15's "class-level declarations"
summary.  The actual declaration in Form1.vb is:

    <DllImport("libgmp-10.dll", EntryPoint:="__gmp_set_memory_functions",
               CallingConvention:=CallingConvention.Cdecl)>
    Private Shared Sub GmpSetMemoryFunctionsNative(
        allocFn As IntPtr, reallocFn As IntPtr, freeFn As IntPtr)
    End Sub


================================================================================
SECTION 16 — PASS 2 ALIASING BUG IN THREE-WAY SPLIT MULTIPLY
================================================================================

Problem
-------
After the VirtualAlloc custom allocator was installed (Section 14), the 3-way
split multiply (Section 13) could successfully complete Pass 0 and Pass 1 on
full billion-digit runs.  Pass 2 continued to crash silently — no managed
exception, no log entry, just process termination — consistent with GMP
calling abort() internally.

The log entry pattern:

  [ComputePi] Pass 2: gmpNumer *= Q2 in-place  RAM:466MB
  <process terminates — no further entries>

The log message for Pass 2 is written BEFORE mpz_mul executes (same pattern
as all the other pre-operation trace entries), so the crash was inside
mpz_mul(gmpNumer, gmpNumer, mpQ2).

Root cause — mpz_t is a value type in GMP.NET
----------------------------------------------
Math.Gmp.Native declares mpz_t as a Structure (value type, not a reference
type).  When a VB variable of type mpz_t is passed as a function argument
it is copied by value onto the call stack.

The call:
    gmp_lib.mpz_mul(gmpNumer, gmpNumer, mpQ2)

passes THREE stack copies of the struct: one for the destination parameter
and two for the source parameters.  The destination copy and the first source
copy are independent stack addresses even though they contain the same field
values (same native pointer to gmpNumer's limb buffer).

GMP's internal aliasing detection compares the struct addresses — or more
precisely, the pointer-to-mpz_t values — passed for dst and op1:
    if (dst == op1) { /* alias: allocate temp copy first */ }
Because the two struct copies are at different stack addresses, GMP sees
dst != op1, concludes there is no aliasing, and proceeds to write the result
directly into the output buffer while still reading from it.  The buffer is
simultaneously being read as source data and overwritten as output, which
corrupts the multiplication and typically causes an internal assertion
failure or a heap write past the allocated limb region — GMP responds to
both by calling abort().

This also explains why Pass 0 and Pass 1 never crashed: they used separate
output variables (mpR0, mpR1), so dst and src1 were genuinely different
mpz_t values with different native pointers, and no aliasing occurred.

Fix — separate output variable with mpz_swap
--------------------------------------------
Pass 2 was changed to use a separate mpz_t variable for the output:

    Old (crashed):
        gmp_lib.mpz_mul(gmpNumer, gmpNumer, mpQ2)   ' aliased dst == src1
        gmp_lib.mpz_clear(mpQ2)

    New (fixed):
        Dim mpR2 As New mpz_t()
        gmp_lib.mpz_init(mpR2)
        gmp_lib.mpz_mul(mpR2, gmpNumer, mpQ2)       ' dst != src1 — no aliasing
        gmp_lib.mpz_clear(mpQ2)
        gmp_lib.mpz_swap(gmpNumer, mpR2)            ' put result in gmpNumer (O(1))
        gmp_lib.mpz_clear(mpR2)                     ' frees old gmpNumer buffer

mpz_swap exchanges the native pointers inside the two mpz_t structs without
moving any limb data.  After the swap, gmpNumer holds the result and mpR2
holds the old (now obsolete) gmpNumer buffer.  mpz_clear(mpR2) immediately
frees that ~208 MB buffer, which reduces peak RAM during the combine phase
shifts that follow.

Side-effect: reduced peak RAM during combine
--------------------------------------------
The original in-place multiply kept the old 208 MB gmpNumer buffer alive
until after the combine phase.  With the separate-variable approach that
buffer is freed by mpz_clear(mpR2) before the combine shifts begin.  The
first shift (r2 << k) allocates ~574 MB; previously it had to do so while
the old 208 MB buffer was still present.

Code location
-------------
Form1.vb — ComputePiGMP, Pass 2 block (was labelled
"Pass 2: gmpNumer *= Q2 (in-place; gmpNumer becomes r2)").


================================================================================
SECTION 17 — COMPLETE UPDATED SUMMARY OF ALL MODIFIED CODE LOCATIONS
================================================================================

(Supersedes Sections 12 and 15)

ApplicationEvents.vb
  - MyApplication_UnhandledException: implemented (was empty stub)
  - Writes full exception chain to log file before showing dialog

Form1.vb — file header
  - Removed #Const DETAILED_LOGGING
  - Added #Const LOGGING_DETAIL = 1 with three-level description (Section 11)

Form1.vb — class-level declarations
  - DiskNode: changed from Class to Structure
  - DiskNode.MemoryNode: replaced with MemP, MemQ, MemT separate fields
  - _nativeCrashCallback: new field (NativeCrashFilterCallback delegate)
  - SetUnhandledExceptionFilter: new P/Invoke declaration
  - NativeCrashFilterCallback: new Delegate type
  - HandleNativeCrash: new crash callback method
  - GMP_LARGE_THRESHOLD constant (512 KB)
  - VirtualAlloc, VirtualFree, CopyMemory P/Invoke declarations
  - MEM_COMMIT_RESERVE, MEM_RELEASE, VA_PAGE_READWRITE constants
  - GmpSetMemoryFunctionsNative: direct P/Invoke to __gmp_set_memory_functions
    in libgmp-10.dll (bypasses Math.Gmp.Native wrapper; see Section 14-D)
  - _gmpAlloc, _gmpRealloc, _gmpFree Shared fields (GC anchors for custom
    allocator delegates)
  - _savedGmpAlloc, _savedGmpRealloc, _savedGmpFree Shared fields (GMP's
    original CRT allocators, used as fallback for small allocations)
  - GmpAllocFunc, GmpReallocFunc, GmpFreeFunc Shared allocator methods
    (small allocs delegate to _savedGmpAlloc; large allocs use VirtualAlloc)
  - InitGmpVirtualAllocFunctions() method

Form1.vb — Form1_Load
  - InitGmpVirtualAllocFunctions() called before dummy mpz_t init
  - AppDomain.CurrentDomain.UnhandledException subscription
  - SetUnhandledExceptionFilter registration

Form1.vb — new methods added
  - OnAppDomainUnhandledException
  - WriteToLog
  - WriteExceptionToLog
  - SerializeOneMpz  (extracted helper for SerializeNodeToDisk)
  - DeserializeOneMpz (extracted helper for LoadNodeFromDisk)

Form1.vb — BtnCompute_Click
  - All catch blocks now call WriteExceptionToLog before MessageBox
  - Log file header includes the LOGGING_DETAIL mode description

Form1.vb — BinarySplitChunk
  - Stack and Dictionary are now pre-sized
  - Entry/exit WriteToLog calls present (LOGGING_DETAIL = 2 only)
  - Try/Catch logs failing WorkItem on exception

Form1.vb — SerializeNodeToDisk
  - Signature: takes p, q, t As mpz_t + Optional detailLog As Boolean = True
  - Implementation: 64 KB SOH staging buffer via SerializeOneMpz (no LOH)
  - Logging gated on LOGGING_DETAIL level and detailLog parameter

Form1.vb — SerializeOneMpz
  - Pre-allocates Marshal.AllocHGlobal buffer from mpz_sizeinbase
  - Passes buffer to mpz_export directly (no GMP-internal allocation)
  - gmp_lib.free removed; Marshal.FreeHGlobal in Finally block
  (Required after VirtualAlloc custom allocator was installed — GMP's
  NULL-dest mpz_export would allocate via VirtualAlloc but gmp_lib.free
  would call CRT free on that pointer, causing immediate crash.)

Form1.vb — LoadNodeFromDisk
  - Signature: ByRef p, q, t + Optional detailLog As Boolean = True
  - Implementation: Marshal.AllocHGlobal + 64 KB staging (no pinned arrays)
  - "Loading" entry written at start; "Done" entry written on success
  - Logging gated on LOGGING_DETAIL level and detailLog parameter

Form1.vb — BinarySplitGMP (disk-mode combine loop)
  - Return type: List(Of Result); all Tuple references removed
  - DiskNode struct fields MemP/MemQ/MemT used throughout
  - isLastLevel = (currentSize <= 2) set at start of each level iteration
  - SerializeNodeToDisk and LoadNodeFromDisk called with isLastLevel
  - GC.Collect every 10 pairs removed; between-level GC.Collect retained
  - Chunk progress logging every 100 (was 1,000)
  - Early-free: each input operand freed after its last multiply (Section 6)
  - In-place add: mpz_add(tempA, tempA, tempB) replaces separate newT alloc
    (Section 9); newT variable removed; tempA used directly as T result

Form1.vb — ComputePiGMP (final arithmetic)
  - nodes: List(Of Result) throughout; all Tuple references removed
  - Final combine loop: same early-free and in-place-add pattern as above
  - gmpOne freed (mpz_clear + mpz_init) immediately after gmpSqrtInput
    = gmpOne^2; saves ~198 MB during sqrt and subsequent operations
  - gmpSqrt and finalP freed together before the large multiply;
    saves ~538 MB off the multiply baseline
  - finalT spilled to DISK_CACHE_DIR\finalT_spill.bin before the multiply,
    reloaded with mpz_init + DeserializeOneMpz after; saves ~548 MB
  - gmpNumer *= finalQ implemented as 3-way split multiply (Section 13):
      finalQ split into Q0, Q1, Q2 (equal bit-thirds); Q1 and Q2 spilled
      to disk; sub-multiplies produce r0, r1, r2 with combine via shift-and-add
  - Pass 2 uses a separate mpR2 output variable to avoid GMP.NET aliasing
    bug (Section 16); mpz_swap + mpz_clear(mpR2) put result in gmpNumer
    and immediately free the old ~208 MB gmpNumer buffer
  - mpz_clears at end of function updated accordingly
  - All [ComputePi] WriteToLog calls gated on LOGGING_DETAIL >= 1
  - Node P/Q/T size logging gated on LOGGING_DETAIL >= 1
  - Exception catch block calls WriteExceptionToLog

Form1.vb — DisplayTimer_Tick
  - displayStr = Nothing after streaming completes (releases ~2 GB LOH block)


================================================================================
END OF CHANGE LOG
================================================================================
