# Changelog

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
