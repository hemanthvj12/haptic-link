# Memory trim (branch `memory-trim`, work in progress)

Measured 2026-11-03 (Task Manager's figure, private working set): tray `HapticLink` ~20 MB, `HapticLink.Service` ~28 MB.

## Done on this branch (no behaviour change)
- Both projects: `ConcurrentGarbageCollection=false` (no background GC thread) and `System.GC.ConserveMemory=5`.
- Not built or measured yet: build, install the dev service, compare the numbers above.

## Next (owner chose: no Rust rewrite)
1. Measure the change above. If worth it, bump the service `<Version>` before releasing.
2. Optional, low risk: tray `InvariantGlobalization`; hand memory back after startup / setup window close.
3. Native AOT for the service (~5–10 MB): convert the ~20 `[ComImport]` interfaces (Audio.cs, ComObjects.cs, HapticsRelay.cs) to `[GeneratedComInterface]`. Needs an in-game haptics/speaker retest.
4. Tray without WinForms (~3–5 MB): only if still wanted after 1–3.

Delete this file before merging.
