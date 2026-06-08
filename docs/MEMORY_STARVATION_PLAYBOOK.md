# Memory-Starvation Playbook (#124)

A reusable runbook for the **memory-starvation slowdown** — the failure mode where a run is
*correct* but runs many× slower because the `MemoryBudget` governor trades speed for OOM-safety
under RAM pressure. Codified from the 2026-06 cross-machine investigation (a peer's 5 B took
16 h 30 m in binary-split alone; a local 5 B sat 15 h+ on a single combine level).

> **It is a silent slowdown, not a crash** — the result is still bit-exact. You must recognise it
> deliberately; nothing fails loudly.

---

## 0. First, rule out the *non*-starvation cause

The top binary-split combine levels **legitimately** run at **DOP = 3** by the §231 policy at 5 B
(3 of 24 cores) — that is a RAM-safety cap, **not** starvation (see #121, and §273/§274/§275 which
route those multiplies through the chunked grid to break the cap). Starvation is when the governor
goes *further* and serialises to DOP = 1 / trims pools under live pressure.

---

## 1. Recognise the signature

**Phase log (`pi_phase_log.txt`):**

| Marker | Meaning |
|--------|---------|
| `[MemoryBudget§70] §gen DOP=1 peak X > budget — routing AxB to chunked-grid full product (RAM cap)` | A hot-path multiply forced to **serial** chunked-grid. |
| `[MemoryBudget§243] §gen DOP floored N→M (… availPhys=Y …)` | DOP trimmed under live pressure. |
| `[MemoryBudget§243] pressure trim (commit … < … GB)` | Pool trimmed. |
| Combine levels **ballooning** | L14/L15/L16 taking many hours, far slower than the level-doubling trend. |

Quick count:

```powershell
Select-String -Path pi_phase_log.txt -Pattern 'RAM cap|floored' | Measure-Object | % Count
```

**Zero = healthy; a climbing count = starved.**

**System (via `Mike-MemDiag.ps1` or the run sampler):**

- `availPhys` collapsing toward the **5 GB governor headroom**.
- Process **commit ≫ working set** (e.g. 64 GB commit / 42 GB RSS ⇒ ~20 GB paged out ⇒ thrashing).
- An external process holding a large RAM slice (browser / game / VM / AV reindex).

---

## 2. The mechanism (so the numbers make sense)

`MemBudget_ShouldFallbackToChunkedGrid` (§70, `Form1.vb`) routes a multiply to serial chunked-grid
when:

```
ProjectMulPeak(szA, szB, DOP=1)  >  budget = min(availPhys, availCommit) − headroom (5 GB)
```

When `availPhys` falls to ≈ the headroom, **budget → ~0**, so even a 0.6 GB multiply "doesn't fit"
and serialises → throughput collapse. The §243 floor trims DOP the same way.

> **Log caveat:** the `RAM:` column is `WorkingSet64` sampled at *level boundaries* (memory
> troughs), so it **undercounts** the mid-merge peak. The log can look calm (~15 GB) while the real
> peak that tripped the governor was ~48 GB.

**Per-scale peaks (observed):** ~5 GB @ 1 B · **~40–48 GB @ 5 B** (top combine merges + divide).

---

## 3. Diagnose (the flow)

1. Run **`Mike-MemDiag.ps1`** mid-run (one-shot, or `-Samples N -IntervalSec S` to watch): captures
   physical RAM, **commit limit (RAM + pagefile)**, pagefile config, and top processes by
   commit / working-set — names any external hog.
2. Compare `availPhys` against the scale's expected peak (§2). If `availPhys < peak + 5 GB`, the
   governor will throttle.
3. Check the **pagefile / commit limit** (`Get-CimInstance Win32_PageFileUsage` /
   `Win32_PageFileSetting`): commit limit = RAM + pagefile; hitting it is the §238 OOM signature.
4. Watch the trend with the run sampler (cores / `availPhys` / `RAM cap|floored` count over time).

---

## 4. Fix (in order of likelihood)

1. **Run on an otherwise-idle box.** Close the external hog (browser / game / VM / AV). This is the
   #1 cause — confirmed twice (a ~9 GB game and a ~40 GB overnight hog both triggered it).
2. **Raise the pagefile** if commit-limited (fixed 32–64 GB on SSD) — relieves §238 OOM, though
   physical-RAM pressure still throttles speed.
3. **Genuinely RAM-limited host** (can't free enough): reduce digit count; or run disk-mode
   (`-Threshold` low) accepting slower I/O; or rely on the combine-perf work (#121/#122/#123,
   §273/§274/§275) that lowers per-task RAM / raises parallelism within budget.
4. **Resume, don't restart.** If a starved run is killed, resume from the latest `snap_L*` on a
   clean box — never redo from scratch.

> **Do NOT raise `PI_MEMBUDGET_HEADROOM_GB`** — that makes the governor *more* conservative and
> serialises *more*. The lever is free RAM, not the headroom.

---

## 5. Prevent

- **Pre-flight check (#120):** warn before the run starts when `availPhys < projected peak +
  headroom` — dialog on direct launch, `[WARN]` / opt-in exit on headless. Uses the same §70/§243
  math so the warning predicts the governor's actual decision.
- Run baselines via `Run-Baseline.ps1` on an idle box.

---

## Tooling

| Script | Role |
|--------|------|
| `Mike-MemDiag.ps1` | During-run memory probe: physical RAM, commit limit, pagefile, top consumers. |
| `Run-Baseline.ps1` | One-command baseline launcher (idle-box assumption; auto-named output + bundle). |

## Related

#120 (pre-flight check) · #121/#122/#123 + §273/§274/§275 (combine/numerator/sqrt under-parallelization
that *compounds* the slowdown) · #88 (bandwidth / structural limits) · #109 (CIM DRAM probe).
