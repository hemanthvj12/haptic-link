# Changelog

## 1.0.0 (2026-11-02)
- First release: haptics, adaptive triggers, rumble and light bar for a DualSense connected over Bluetooth.
- Tray switch "Full DualSense features", remembered; the feature starts by itself when the controller connects.
- One administrator prompt on first start sets up the background service and the virtual controller driver.
- Haptics are sent in step with the game's audio, and a report that's a moment late is sent at once instead of leaving a gap.
- The service log shows haptics timing every 30 seconds while haptics play.
- Updates itself from GitHub releases.
- A control pipe lets PeripheralHub show and change the switch.
- Service 1.0.0.
