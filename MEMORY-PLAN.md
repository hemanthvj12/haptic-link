# Memory trim (branch `memory-trim`, work in progress)

Figures are Task Manager's (private working set), bridge on with haptics running.

## Measured 2026-11-03 (fresh starts, ~3 min each)
- Tray: release 1.2.3 ~14 MB → this branch ~10–11 MB. (A tray that had run for hours showed ~20 MB: it grows over time; not checked whether the branch build does too.)
- Service: ~31 MB both with and without the same settings, so they were taken back out of the service. Its memory isn't the managed heap: it climbs from ~14 to ~31 MB in the first 2 minutes after start (JIT, audio/COM, ~26–30 threads), then stays flat.

## On this branch
- Tray only: `ConcurrentGarbageCollection=false` and `System.GC.ConserveMemory=5`. No behaviour change.

## Decision (owner, 2026-11-03): the tray first, split in two
"It should not take any resources; all work is being done by the service anyway." No Rust rewrite.
- `HapticLink.exe`: new always-on tray, C# with Native AOT and plain Win32 (Shell_NotifyIcon, popup menu), no WinForms: icon + menu, STATUS poll, ATTACH connection, Start with Windows, double-input check, Velopack install/uninstall hooks. Two ready-made `.ico` files (active/idle) instead of drawing them at startup. Target ~1–3 MB (now ~14).
- `HapticLink.Setup.exe`: today's WinForms setup window, unchanged, started only when needed (first start, "Set up HapticLink…", "Needs setup", Repair) and closed afterwards.
- Update check: out of the always-on tray, in a short-lived process once a day.
- Kept: exe name, Run-key entry, install folder, pipe protocol. The service isn't touched (no in-game retest).
- Check first: whether Velopack's library works under Native AOT; if not, install/update steps go into the setup exe.
- Retest: every menu item, the setup window in each case, Exit turning HapticLink off, one real update from a local package.
- Not started yet. The GC settings above become moot for the tray once it's AOT (keep them for the setup exe).

## Later
- Native AOT for the service (~31 → ~5–10 MB): the ~20 `[ComImport]` interfaces (Audio.cs, ComObjects.cs, HapticsRelay.cs) to `[GeneratedComInterface]`, then an in-game haptics/speaker retest.

How it was tested: `artifacts\memtrim-swap.ps1` (not in git) stops the service, swaps in the published exe and starts it; `-Restore` puts the original back (kept in `artifacts`). The installed service and tray are the 1.2.3 release again.

Delete this file before merging.
