# Changelog

## 1.3.1 (2026-11-07)
- The background service takes far less memory: about 10 MB while the controller is connected (about 13 MB while the controller speaker plays game sound), instead of about 30 MB. Haptics, adaptive triggers, rumble, lights and the speaker work exactly as before.
- Service 1.3.1: after updating, HapticLink asks once for administrator rights to update its background service.

## 1.3.0 (2026-11-06)
- The tray app takes far less memory: about 5 MB in Task Manager instead of about 14 MB (and up to 20 MB after running for hours), and it no longer grows over time. It's rebuilt as a small native program; the background service, which does the actual work, is unchanged.
- The setup window is now its own program, `HapticLink.Setup.exe`, started only when it's needed (first start, *Set up HapticLink…*, something missing, Repair) and closed afterwards, so it isn't kept in memory.
- Update checks run in a short helper that exits when it's done, so checking for updates doesn't leave extra memory in use. *Restart to update* works as before.
- The tray menu follows Windows' light or dark mode. Its items no longer show hover tips.
- No administrator prompt after updating: the background service didn't change.

## 1.2.3 (2026-11-02)
- Haptics no longer need Steam Remote Play to have been set up once: when Steam's speaker driver isn't installed yet, HapticLink installs it from your own Steam folder (Steam doesn't have to be running).
- If Steam's speaker driver gets removed while Steam is still installed, HapticLink puts it back, and it keeps using its own speaker device instead of leaving an extra "Speakers" device in Sound settings.
- A setup window, step by step: it checks for the two free apps HapticLink needs, HidHide and Steam, each with a green tick or a red cross and a link to get it, and then installs HapticLink with one administrator prompt, showing each step as it finishes. It opens on first start, after updates that need it, and from the tray menu (*Set up HapticLink…*, shown while something is missing).
- HapticLink no longer downloads and installs HidHide itself (added in 1.2.2): you install it, and setup sets it up.
- If HidHide or Steam's speaker driver goes missing later (uninstalled), HapticLink stays off instead of running half-working, and opens the setup window showing what's missing. Once both have a green tick it turns back on by itself, without setting up again.
- Double input is caught and repaired: if games can see your controller twice (the real one is back in Windows' game controller list, or HidHide's settings were changed, for example cloaking turned off by another app), HapticLink turns off and the setup window shows *HidHide set-up* with a red cross and a **Repair** button, which hides the controller again and restarts it.
- Service 1.2.3: after updating, HapticLink asks once for administrator rights to update its background service.

## 1.2.2 (2026-11-02)
- Setup offers to install HidHide when it's missing: one question, then HapticLink downloads HidHide 1.5.230 from Nefarius's GitHub release, checks it's the exact file, and installs it within the same administrator prompt. Said no? The tray menu has *Install HidHide (admin prompt)…* while it's missing.
- Service 1.2.2: after updating, HapticLink asks once for administrator rights to update its background service (and offers HidHide then, if it's missing).

## 1.2.1 (2026-11-02)
- Fixed: games and gamepad testers could see two controllers, the real one not responding, even with HidHide installed (issue #1). HidHide only hides a controller from apps that open it afterwards, and Windows' own game controller input had already opened it. HapticLink now restarts the controller for a moment after hiding it (and again when it stops, so games see it once more), then checks that HidHide is really attached to it.
- If games can still see the real controller, the tray says why: HidHide isn't installed, HidHide needs a Windows restart after installing, or the controller couldn't be restarted (then turn it off and on).
- Works with HidHide's inverse mode turned on (it's turned off while HapticLink runs and put back afterwards), and when another app (DSX, DS4Windows) had already hidden the controller without letting HapticLink's service see it.
- Service 1.2.1: after updating, HapticLink asks once for administrator rights to update its background service.

## 1.2.0 (2026-11-02)
- HapticLink only works while its tray app runs: Exit (or the app not starting) turns it off, and games get the controller back as it is. The switch is remembered for the next start.
- PeripheralHub hides its "Full DualSense features" switch while the HapticLink app isn't running.
- Service 1.2.0: after updating, HapticLink asks once for administrator rights to update its background service.

## 1.1.0 (2026-11-02)
- The controller's speaker works over Bluetooth: sounds games play on the controller (Stellar Blade's parry sound, for one) come out of its speaker, or of headphones plugged into it.
- New tray switch "Controller speaker" (on by default) to turn that off.
- The controller's sound device is now called "DualSense Controller (HapticLink)" in Windows' Sound settings, instead of "Wireless Controller".
- Apps can read and change the speaker switch over the control pipe (`FEATURES`, `SET speaker 0|1`).
- Service 1.1.0: after updating, HapticLink asks once for administrator rights to update its background service.

## 1.0.1 (2026-11-02)
- The tray says "on (no haptics)", with the reason, when haptics can't be recorded, instead of claiming they work; it goes back to normal by itself once recording works. PeripheralHub sees the same.
- Fixed: if the controller's speaker device had been removed, HapticLink could mistake its leftover entry for a working speaker and set up the new one without its driver, so haptics never started.
- Service 1.0.1: after updating, HapticLink asks once for administrator rights to update its background service.

## 1.0.0 (2026-11-02)
- First release: haptics, adaptive triggers, rumble and light bar for a DualSense connected over Bluetooth.
- Tray switch "Full DualSense features", remembered; the feature starts by itself when the controller connects.
- One administrator prompt on first start sets up the background service and the virtual controller driver.
- Haptics are sent in step with the game's audio, and a report that's a moment late is sent at once instead of leaving a gap.
- The service log shows haptics timing every 30 seconds while haptics play.
- Updates itself from GitHub releases.
- A control pipe lets PeripheralHub show and change the switch.
- Service 1.0.0.
