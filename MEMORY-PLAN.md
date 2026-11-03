# Memory trim (branch `memory-trim`, work in progress)

Figures are Task Manager's (private working set), bridge on with haptics running.

## Measured 2026-11-03 (fresh starts, ~3 min each)
- Tray: release 1.2.3 ~14 MB → this branch ~10–11 MB. (A tray that had run for hours showed ~20 MB: it grows over time; not checked whether the branch build does too.)
- Service: ~31 MB both with and without the same settings, so they were taken back out of the service. Its memory isn't the managed heap: it climbs from ~14 to ~31 MB in the first 2 minutes after start (JIT, audio/COM, ~26–30 threads), then stays flat.

## On this branch
- Tray only: `ConcurrentGarbageCollection=false` and `System.GC.ConserveMemory=5`. No behaviour change.

## Next (owner chose: no Rust rewrite)
1. Check the tray after a few hours (does it still creep to ~20 MB?).
2. Optional, low risk: tray `InvariantGlobalization`; hand memory back after the setup window closes.
3. The service is where the rest is. Native AOT (~5–10 MB) needs the ~20 `[ComImport]` interfaces (Audio.cs, ComObjects.cs, HapticsRelay.cs) converted to `[GeneratedComInterface]` and an in-game haptics/speaker retest.
4. Tray without WinForms (~3–5 MB): only if still wanted after 1–3.

How it was tested: `artifacts\memtrim-swap.ps1` (not in git) stops the service, swaps in the published exe and starts it; `-Restore` puts the original back (kept in `artifacts`). The installed service and tray are the 1.2.3 release again.

Delete this file before merging.
