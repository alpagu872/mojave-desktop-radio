# Installation guide

## 1. Requirements

Install these first (all are standard for a modern New Vegas setup):

* xNVSE 6.3 or newer, JIP LN NVSE, ShowOff xNVSE, UIO
* The Mod Configuration Menu + MCM Extender (only needed for the in-game settings page)
* **.NET 8 Desktop Runtime x64** — https://dotnet.microsoft.com/download/dotnet/8.0
  ("Desktop Runtime", Windows x64). Without it the BridgeHost cannot start.
* Spotify **desktop** app (the Microsoft Store version works too; the web player does not)

## 2. Install the mod

**Mod Organizer 2**: *Install a new mod from an archive* → pick `MojaveDesktopRadio-<version>.zip` →
enable the mod → in the *Plugins* tab tick `MojaveDesktopRadio.esp`.
Priority/load order do not matter: the mod has no file conflicts and its ESP only requires `FalloutNV.esm`.

**Vortex**: drag the archive into Vortex, install, enable, deploy. Make sure the ESP is enabled.

**Manual**: extract the archive into `Fallout New Vegas\Data` (the archive's top level is the Data folder).

The archive contains:

```
MojaveDesktopRadio.esp
NVSE\Plugins\MojaveDesktopRadio.dll / .ini     plugin + technical settings and Pip-Boy texts
NVSE\Plugins\Scripts\ln_MojaveDesktopRadio.txt load script (JIP script runner)
NVSE\user_defined_functions\MojaveDesktopRadio\ UDFs
Menus\Prefabs\MojaveDesktopRadio\              Pip-Boy panel (inserted by UIO)
UIO\Public\MojaveDesktopRadio.txt
MCM\MojaveDesktopRadio.json, Config\MojaveDesktopRadio.ini   MCM page + its settings file
MojaveDesktopRadio\BridgeHost\                 desktop companion (started automatically)
MojaveDesktopRadio\Docs\
```

## 3. Optional: a device for the Radio FX

The FX needs an output device that nobody listens to, so the original sound is not heard twice.
The BridgeHost picks the first one it finds, in this order:

1. **VB-CABLE** ("CABLE Input") — free from https://vb-audio.com/Cable/. Run the installer as
   administrator, reboot. Recommended.
2. **Steam Streaming Speakers** — installed with Steam. Works out of the box, but if you use Steam
   Remote Play the streamed audio would include Spotify while the station is on.

Never make the virtual device your Windows default output. To use another device, set `routeDeviceId`
in `%LOCALAPPDATA%\MojaveDesktopRadio\settings.json` (see `mdr devices`).

Without such a device the mod still works; the preset is shown as `(NO ROUTE - CLEAN)`.

## 4. First start

1. Start Spotify and play any track once (it registers with Windows on its first playback).
2. Launch the game through NVSE / your mod manager and load a save.
3. Within a few seconds the green radio icon appears in the notification area (the BridgeHost).
4. Pip-Boy → DATA → RADIO → **Mojave Desktop Radio**.

The BridgeHost starts automatically after a save is loaded and exits a few seconds after the game
closes (MO2 stays "locked" for those seconds). You can also start it yourself
(`Data\MojaveDesktopRadio\BridgeHost\MojaveDesktopRadio.exe`); it then runs until you exit it from the tray.

## 5. What is written where

| File | When |
|---|---|
| `Data\Config\MojaveDesktopRadio.ini` | MCM changes (MCM Extender saves it when the pause menu closes) |
| `<game folder>\MojaveDesktopRadio.log` | each game start (like other NVSE plugin logs) |
| `%LOCALAPPDATA%\MojaveDesktopRadio\settings.json` | created once on first start (BridgeHost settings) |
| `%LOCALAPPDATA%\MojaveDesktopRadio\logs\` | BridgeHost event log, rotated at 1 MB |
| `%LOCALAPPDATA%\MojaveDesktopRadio\state\` | only while Spotify is re-routed / muted (restore markers) |

No audio is ever written to disk. Under MO2 nothing goes to *Overwrite*.

## 6. Updating

Exit the BridgeHost from the tray, replace the mod with the new archive, start the game. Settings are kept
(`Config\MojaveDesktopRadio.ini` — MO2 asks whether to merge/replace; replacing resets MCM options to defaults).

## 7. Uninstalling

Tray icon → **Exit (restores audio routing)** → disable/remove the mod. Saves are not affected (the
station only exists in the ESP). Delete `%LOCALAPPDATA%\MojaveDesktopRadio` if you want.
