# Troubleshooting

Two logs tell almost everything:

* `<game folder>\MojaveDesktopRadio.log` — the NVSE plugin (`bDebugLog = 1` in `Data\NVSE\Plugins\MojaveDesktopRadio.ini` for more)
* `%LOCALAPPDATA%\MojaveDesktopRadio\logs\bridgehost.log` — the BridgeHost (`"verboseLog": true` in `settings.json` for more)

`Data\MojaveDesktopRadio\BridgeHost\mdr.exe` is a diagnostic tool (run it from a command prompt;
with MO2 it is in the mod's own folder, e.g. `MO2\mods\Mojave Desktop Radio\MojaveDesktopRadio\BridgeHost\`):

| Command | Shows / does |
|---|---|
| `mdr status` | Spotify detection, current track, audio devices, routing state |
| `mdr devices` | output device and the device used for the Radio FX |
| `mdr toggle` / `next` / `prev` | controls Spotify only (never other players) |
| `mdr watch 60` | prints every Spotify change for 60 s |
| `mdr loopback-test` | checks that the FX device can be captured (inaudible test tone) |
| `mdr dsp-test` | offline check of all presets |
| `mdr pipe-test` | talks to a running BridgeHost |

## Symptoms

| Symptom | Cause / fix |
|---|---|
| Message box "MojaveDesktopRadio.dll is missing or outdated" | the DLL did not load: game must be 1.4.0.525 with xNVSE 6.3+; check `nvse.log` for `MojaveDesktopRadio.dll`. Old DLL from an earlier version → reinstall the mod. |
| Message box "requires JIP LN NVSE / ShowOff" | install the named requirement |
| Station missing from the Pip-Boy list | `MojaveDesktopRadio.esp` not active, or merged into a merged patch (not supported). The plugin log shows `station reference XX000802 (mod index N)` when the ESP was found. |
| Station listed but no panel | UIO missing/outdated; `ui_organizer.log` must list `MojaveDesktopRadio\Panel.xml`. The panel only appears while the station is **tuned** (square marker). |
| `SIGNAL LOST - BRIDGE OFFLINE` | BridgeHost not running. Install the .NET 8 Desktop Runtime x64. Start `Data\MojaveDesktopRadio\BridgeHost\MojaveDesktopRadio.exe` by hand and look at its log. The plugin log says `BridgeHost launched: <path>` or why not. |
| `SIGNAL LOST` + `DESKTOP PLAYER NOT FOUND` | Spotify is not running |
| `STATUS: PRESS PLAY IN SPOTIFY ONCE` | Spotify has not played anything since it started; press play once in Spotify |
| Buttons do nothing | look for `media Toggle: …` lines in the BridgeHost log. `NoSession` → see above. No lines at all → the click did not reach the plugin: check `nvse.log` for errors mentioning `MojaveDesktopRadio`. |
| Corner message "MCM settings not loaded" | a script did not compile (usually after editing the UDFs by hand). `nvse.log` shows `Failed to precompile … MojaveDesktopRadio\…`. Reinstall the mod. |
| MCM change has no effect | the BridgeHost log must show `game config: … preset=…` after the change. If not, see the line above. |
| `FX MODE: … (NO ROUTE - CLEAN)` | no inaudible device found (install VB-CABLE) or automatic routing failed (reason in the BridgeHost log). Manual alternative: Windows Settings → System → Sound → Volume mixer → Spotify → Output device = *CABLE Input*, and set `"autoRouteSpotify": false` in `settings.json`. |
| `FX MODE: … (TUNING...)` stays | Spotify's stream did not move to the FX device yet; pause and play once in Spotify |
| No Spotify sound at all (even outside the game) | exit the BridgeHost from the tray (this restores routing). Still silent: Windows Settings → Volume mixer → Spotify → Output device = **Default**. |
| Music sounds doubled / echo | the FX device is audible: do not enable "Listen to this device" on CABLE Output, do not make the virtual device your default output, or Steam Remote Play is streaming |
| Wrong characters (`?`) on the Pip-Boy | the font has no glyph, or the game text uses another code page than Windows: set `iCodePage` (1252 Western, 1254 Turkish, 1251 Cyrillic …) |
| Music keeps playing in the pause menu | MCM → Behaviour → Pause Menu = Pause / Lower Volume |
| Gamepad cannot select the panel | by design; use MCM → Hotkeys |
| MO2 stays locked a few seconds after quitting | the BridgeHost restores audio routing and exits ~3 s after the game |

## Reporting a bug

Please attach both logs, `nvse.log` and the output of `mdr status`, and describe what you clicked.
