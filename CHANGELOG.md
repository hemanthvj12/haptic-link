# Changelog

## 1.2.2 (unreleased)
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
