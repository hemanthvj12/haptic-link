# HapticLink for DualSense

Games only use a DualSense's **haptics, adaptive triggers, rumble and light bar** when it's plugged in with a cable. HapticLink makes them work over **Bluetooth** on Windows: games see a wired DualSense, and everything they send is passed on to your controller over Bluetooth.

- Haptics (the fine vibrations games play as sound), adaptive triggers, rumble, light bar and player lights.
- The controller's speaker, and headphones plugged into the controller.
- Works with any game that supports a wired DualSense (tested with Stellar Blade).
- One switch in the tray. Turn it on once and it starts by itself whenever your DualSense connects.
- Updates itself from this page's releases.

## Install

1. Download `HapticLink-win-Setup.exe` from the [latest release](https://github.com/hemanthvj12/haptic-link/releases/latest) and run it. Windows may warn that it's from an unknown publisher (the installer isn't code-signed): choose *More info → Run anyway*.
2. On its first start HapticLink asks once for administrator rights to finish setting up (it installs its background service and the virtual controller driver).
3. Pair your DualSense over Bluetooth if you haven't. HapticLink's tray icon turns blue when it's active.

**You also need:**
- **HidHide** ([Nefarius](https://github.com/nefarius/HidHide/releases)), which hides the real controller from games so they only use the wired one HapticLink gives them (otherwise games drive both and triggers feel wrong). If it isn't installed, HapticLink's setup offers to download and install it for you (or use *Install HidHide…* in the tray menu later).
- **Steam**, for haptics: HapticLink uses Steam's *Streaming Speakers* audio driver as the controller's speaker, and installs that driver from your Steam folder if Steam hasn't yet (you don't need to have used Remote Play, or to keep Steam running). Without Steam, triggers, rumble and lights still work, and the tray menu has a *Get Steam* link.

## Using it

- Click the tray icon for the menu: status, the **Full DualSense features** switch, the **Controller speaker** switch, *Start with Windows*, updates, the log folder.
- The controller speaker is on by default. In Windows' Sound settings it's listed as *DualSense Controller (HapticLink)*; games find it by themselves, so you don't need to pick it. If haptics ever stutter while the controller plays sound, try turning the speaker off.
- The switch is remembered. HapticLink only works while its tray app runs: Exit turns it off (games get the controller back as it is) until you start HapticLink again.
- While it's active, only the virtual controller is visible to games and other apps. Apps that need the real one (for example PeripheralHub for its battery reading) are let through automatically.

## How it works

- A small user-mode driver (UMDF) adds a virtual DualSense that looks like one connected over USB.
- The HapticLink service reads your controller over Bluetooth and feeds its buttons, sticks and motion to the virtual one; what games send back (triggers, rumble, lights) is converted into Bluetooth reports.
- Haptics are audio: games play them on the controller's speaker channels 3 and 4. HapticLink records that audio, resamples it to 3 kHz and streams it to the controller in Bluetooth haptics reports, about 94 a second.
- The controller's speaker sound is on channels 1 and 2. Once a game plays something there, HapticLink encodes it as Opus (the format the controller expects over Bluetooth) and sends it in the same reports as the haptics.
- HidHide hides the real controller from games while the feature is on, and the service puts it back when it stops (also after a crash, at its next start). HidHide only hides a controller from apps that open it afterwards, so the controller is restarted for a moment (about a second) when HapticLink hides it and when it gives it back. If games could still see it, the tray menu says why.

## Uninstall

Settings → Apps → *HapticLink for DualSense* → Uninstall. It asks once for administrator rights to remove the service, driver and virtual speaker.

## For other apps

The service has a control pipe, `\\.\pipe\HapticLink`, open to signed-in users. One line per command, answered with `OK …` or `ERR message`:

| Command | Reply |
| --- | --- |
| `HELLO <app>` | `OK <service version> <protocol>` (protocol is 1). `HELLO PeripheralHub` also lets that app see the real controller while it's hidden. |
| `STATUS` | `OK <state>\|<enabled 0/1>\|<haptics 0/1>\|<controller MAC>\|<message>`; state is `off`, `waiting`, `starting`, `on` or `error` |
| `ENABLE` / `DISABLE` | `OK` (the switch, remembered) |
| `FEATURES` | `OK speaker=<0/1>` (service 1.1.0 and later; more `name=value` pairs may be added) |
| `SET speaker 0` / `SET speaker 1` | `OK` (the controller speaker switch, remembered) |

Only HapticLink writes to the controller while it's active; other apps should ask HapticLink rather than send output reports themselves.

STATUS's haptics flag is 1 while HapticLink is recording the controller's speaker, so a game's haptics reach the controller. While recording fails it's 0 and the message says why (HapticLink keeps retrying; the flag goes back to 1 when it works).

## Building

Needs Visual Studio 2022 or later with the C++ desktop workload and the .NET 8 SDK (the Windows Driver Kit comes from NuGet).

```bash
pwsh drivers/build-service.ps1
```

```bash
dotnet build HapticLink.sln
```

```bash
dotnet test HapticLink.sln
```

`build-installer.ps1 -Version x.y.z` builds the installer into `artifacts\installer`; with `-Install` it also installs that version over this PC's installed copy right away. Releases are built by GitHub Actions when a `vX.Y.Z` tag is pushed; they sign the driver with the release certificate from the repository's secrets. `drivers/make-release-cert.ps1` makes that certificate, keeping a copy next to the repository in `..\Signing\HapticLink` with its password encrypted for your Windows user, so builds on your own PC sign the driver the same way. Without it, builds use a development certificate.

The background service has its own version (`<Version>` in `src/HapticLink.Service/HapticLink.Service.csproj`), separate from the app's. After an update, the app re-installs the service (one administrator prompt) only when the service it carries is newer than the installed one, so updates that don't touch the service install silently. Bump it whenever the service, the driver or its helper change, or users keep the old service. Each changelog entry says which service version it carries.

## Credits

- The virtual controller's report descriptor and feature reports come from [punktfunk](https://git.unom.io/unom/punktfunk) (MIT OR Apache-2.0).
- The driver follows the structure of Microsoft's `vhidmini2` sample.
- Report formats are documented by Linux's `hid-playstation` driver and the DualSense community. The Bluetooth speaker format follows [LinuxAudio4Dualsense5](https://github.com/ebraminio/LinuxAudio4Dualsense5) and DS5_Bridge.
- Opus encoding by [Concentus](https://github.com/lostromb/concentus) (BSD).

## License

MIT, see [LICENSE](LICENSE).
