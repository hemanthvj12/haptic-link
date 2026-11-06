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
- Update check: in a short-lived process after all (see "Updates" below).
- Kept: exe name, Run-key entry, install folder, pipe protocol. The service isn't touched (no in-game retest).
- Check first: whether Velopack's library works under Native AOT; if not, install/update steps go into the setup exe.
- Retest: every menu item, the setup window in each case, Exit turning HapticLink off, one real update from a local package.

## Built (2026-11-06, on this branch)
- `src/HapticLink.Tray` (AOT, Win32), `src/HapticLink.Setup` (WinForms window only), `src/HapticLink.Shared` (linked into both).
- Velopack works under AOT. Updates first stayed in the tray, but its startup check loaded Windows' HTTP/TLS code and the tray stayed at ~10–11 MB, so the check moved to `HapticLink.exe --check-updates` (tray then ~5.5 MB with the check done).
- Memory: ~4.5 MB at start, 5 threads (was ~14 MB, 14 threads). It then grew ~0.35 MB/min (poll garbage; the GC's first budget is sized from the CPU cache): `DOTNET_GCgen0size=100000` kept it flat at 5.3 MB, but AOT only reads that from the environment (`System.GC.Gen0Size` baked into the exe did nothing), so the tray calls `GC.Collect()` once a minute.
- Roughly where the ~5 MB goes (estimate): Windows' own DLLs for the icon and menu (42 loaded) ~1.5–2.5 MB, the .NET runtime ~2–3 MB, HapticLink's data a few KB. A plain C tray would be ~1–2 MB.
- Tested by driving the real tray (Shell_NotifyIconGetRect + real clicks, menu items read with MN_GETHMENU, tip via UI Automation, screenshots): left/right click open the menu (dark mode), status/ticks right, the switch on → "waiting" → off, the speaker switch, Start with Windows (writes this exe; restored), Open log folder, `--setup` opens the window, Exit removes the icon and the service goes "closed". The menu didn't open on 2 of ~45 clicks early on (once right after the setup window closed); 39 tries afterwards, including right after closing setup, all opened it.
- Also tested: the tray opening setup by itself (service stopped: "Finish setting up…" opens it; closing it unfinished exits the tray), and updates (see below).
- With the controller (2026-11-06, installed memtrim.3): bridge on with haptics, the coloured icon and "HapticLink: on" tooltip (real hover, screenshot), the tray's double-input check running every poll with no error and no false alarm (raw input list held only the virtual pad). Not forced: a real double input caught by the tray (same code as before; the service's own HidHide check also catches it).
- Before that the bridge kept failing with "the controller didn't answer": the pad timed out on feature 0x05 even outside HapticLink (HidD_GetFeature error 121). Turning the controller off (hold PS ~10 s) and on fixed it. Not caused by the tray: the service is unchanged 1.2.3 and logged the same error on days before this branch.
- Build: .NET 8's ILCompiler only finds VS 2019/2022; with VS 2026 the publish runs inside vcvars64 with `IlcUseEnvironmentalTools`. NuGet warns about ILCompiler 8.0.19 (GHSA-p3jp-9v83-76w9): the AOT exe carries its runtime, so build releases with a current .NET 8 SDK (CI's `8.0.x` gets the newest).

## Updates (tested 2026-11-06 with local packages)
- 1.2.3 → 1.2.4-memtrim.1 with `Update.exe apply`: both exes in `current`, the Start with Windows entry kept, no setup prompt (same service version), the menu shows Check for updates.
- Check for updates against GitHub: "Up to date" (Velopack under AOT reads GitHub fine).
- memtrim.2 installed with `build-installer.ps1 -Install`: tray ~5.5 MB, 5 threads with the startup check done.
- memtrim.3's package placed in `%LOCALAPPDATA%\HapticLink\packages`: Check for updates → "Restart to update to 1.2.4-memtrim.3" → clicked → memtrim.3 running, attached to the service, old package cleaned up.

## Later
- Native AOT for the service (~31 → ~5–10 MB): the ~20 `[ComImport]` interfaces (Audio.cs, ComObjects.cs, HapticsRelay.cs) to `[GeneratedComInterface]`, then an in-game haptics/speaker retest.

How it was tested: `artifacts\memtrim-swap.ps1` (not in git) stops the service, swaps in the published exe and starts it; `-Restore` puts the original back (kept in `artifacts`). The service is the 1.2.3 release; the app is 1.2.4-memtrim.3 (this branch), which a real 1.2.4 updates over.

Delete this file before merging.
