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
- Update check: planned as a short-lived process; not needed (Velopack works under AOT).
- Kept: exe name, Run-key entry, install folder, pipe protocol. The service isn't touched (no in-game retest).
- Check first: whether Velopack's library works under Native AOT; if not, install/update steps go into the setup exe.
- Retest: every menu item, the setup window in each case, Exit turning HapticLink off, one real update from a local package.

## Built (2026-11-06, on this branch)
- `src/HapticLink.Tray` (AOT, Win32), `src/HapticLink.Setup` (WinForms window only), `src/HapticLink.Shared` (linked into both).
- Velopack works under AOT, so updates stayed in the tray (no separate update process).
- Memory: ~4.5 MB at start, 5 threads (was ~14 MB, 14 threads). It then grew ~0.35 MB/min (poll garbage; the GC's first budget is sized from the CPU cache): `DOTNET_GCgen0size=100000` kept it flat at 5.3 MB, but AOT only reads that from the environment (`System.GC.Gen0Size` baked into the exe did nothing), so the tray calls `GC.Collect()` once a minute.
- Roughly where the ~5 MB goes (estimate): Windows' own DLLs for the icon and menu (42 loaded) ~1.5–2.5 MB, the .NET runtime ~2–3 MB, HapticLink's data a few KB. A plain C tray would be ~1–2 MB.
- Tested by driving the real tray (Shell_NotifyIconGetRect + real clicks, menu items read with MN_GETHMENU, tip via UI Automation, screenshots): left/right click open the menu (dark mode), status/ticks right, the switch on → "waiting" → off, the speaker switch, Start with Windows (writes this exe; restored), Open log folder, `--setup` opens the window, Exit removes the icon and the service goes "closed". Once the menu didn't open on the first click right after the setup window closed.
- Not tested yet: the tray opening setup by itself (needs the service stopped), an update from a local package, the "on" (blue) icon with a controller connected, the double-input check.
- Build: .NET 8's ILCompiler only finds VS 2019/2022; with VS 2026 the publish runs inside vcvars64 with `IlcUseEnvironmentalTools`. NuGet warns about ILCompiler 8.0.19 (GHSA-p3jp-9v83-76w9): the AOT exe carries its runtime, so build releases with a current .NET 8 SDK (CI's `8.0.x` gets the newest).

## Later
- Native AOT for the service (~31 → ~5–10 MB): the ~20 `[ComImport]` interfaces (Audio.cs, ComObjects.cs, HapticsRelay.cs) to `[GeneratedComInterface]`, then an in-game haptics/speaker retest.

How it was tested: `artifacts\memtrim-swap.ps1` (not in git) stops the service, swaps in the published exe and starts it; `-Restore` puts the original back (kept in `artifacts`). The installed service and tray are the 1.2.3 release again.

Delete this file before merging.
