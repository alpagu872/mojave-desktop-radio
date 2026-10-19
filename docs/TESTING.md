# Release checklist

Automated (run by `tools\package.ps1`, all must pass):

* `tools\lint_scripts.py` — scripts, UDFs, MCM JSON, UIO
* `dotnet test` — DSP and protocol unit tests (21)
* `tools\inspect_plugin.py --vanilla` — ESP structure, no overrides, same layout as the vanilla station

Offline, with the BridgeHost running (`mdr` = `dist\BridgeHost\mdr.exe`):

| # | Check | Expected |
|---|---|---|
| O1 | `mdr status` with Spotify open | session `Spotify.exe` found, other players (e.g. Chrome) listed but not chosen, title/artist correct |
| O2 | `mdr toggle`, `mdr next`, `mdr prev` | Spotify reacts, nothing else does |
| O3 | `mdr watch 60` while closing and reopening Spotify | `NotRunning`, then `Playing` again, no crash |
| O4 | `mdr dsp-test`, `mdr loopback-test` | `DSP TEST OK`, `LOOPBACK OK` |
| O5 | `mdr route-test --yes` while Spotify plays | session moves to the FX device, then `restored: True`; `mdr status` → `Persisted device … Default` |
| O6 | `mdr pipe-test` | `state`, `unsupported_version`, `unknown_action` |
| O7 | `mdr_plugin_harness.exe <dll> 15` | `game config: … preset=Mojave`, later `preset=Wasteland`, station ON/OFF, routing restored on exit |
| O8 | same with `--autolaunch` and no BridgeHost running | `BridgeHost launched: …`, BridgeHost exits after the harness |
| O9 | `mdr players` | Spotify `session=Spotify.exe`; the others `running=no` unless installed |
| O10 | harness run (O7) | at 10 s `desktop player (game): Spotify -> foobar2000`, panel `STATUS: FOOBAR2000 NOT RUNNING`; at 11 s back to Spotify with title/artist |

In game (fresh profile with only the requirements + one big profile):

| # | Check | Expected |
|---|---|---|
| G1 | load a save | no message box; `nvse.log` has no `Failed to precompile … MojaveDesktopRadio`; plugin log: `station reference`, `connected to BridgeHost`, `config: …` |
| G2 | Pip-Boy → Radio | station listed; tuning it starts Spotify (auto-play) and shows the panel |
| G3 | PAUSE/PLAY, NEXT, PREVIOUS | Spotify reacts, panel updates, corner message on track change |
| G4 | CHANGE FX MODE | FX line cycles Clean → Mojave → Wasteland → Damaged, sound changes, MCM shows the same preset |
| G5 | MCM: preset, static, volume, custom tone | applied immediately (BridgeHost log `game config: …`) |
| G6 | tune Radio New Vegas | Spotify pauses, only the game radio plays |
| G7 | radio off / on | ~250 ms fade, pause/resume |
| G8 | close Spotify in game, reopen, play | `SIGNAL LOST` → `CONNECTED`, no crash |
| G9 | pause menu with each Pause Menu option | keep / pause / lower |
| G10 | assign a hotkey, use it on the Radio tab and (scope Anywhere) in game | works; unassigned keys untouched |
| G11 | fast travel, load another save, exit to main menu | no hang; station state follows the save |
| G12 | quit the game | Spotify keeps running (paused), routing restored, tray icon gone after ~3 s |
| G13 | FPS with the station on/off | no difference |
| G14 | MCM → Desktop Player → foobar2000 (not installed), then Spotify | panel `STATUS: FOOBAR2000 NOT RUNNING`, Spotify paused and its routing restored; back to Spotify → works as before; tray *Desktop player* greyed out while the game runs |
| G15 | (optional, if installed) another player via MCM | play/pause/next, title on the panel, FX — report results, the author did not test them |

Isolated offline run (does not disturb a game that is running at the same time):
`mdr_plugin_harness.exe dist\Data\NVSE\Plugins\MojaveDesktopRadio.dll 22 --autolaunch --pipe MDR-Test`
— uses its own pipe and starts the bundled BridgeHost with `--no-audio` (log: `bridgehost-MDR-Test.log`).
Copy `dist\Data` elsewhere first: the harness writes a test INI next to the DLL.

Status on the author's PC (VUI+, Pip-Boy 2000 Mk VI, Turkish translation, NVR):
* 0.1.0: O1, O4, O6–O8, G1–G3 (station, panel, PAUSE/NEXT/PREVIOUS, Turkish text) verified.
* 0.2.2: O9/O10 verified offline (player switch, texts, settings.json untouched in test mode).
  **G14 must be confirmed in game; G15 depends on players the author does not use.**
* 0.2.0: O7/O8 verified with the release layout (settings delivered, preset change applied in 1 ms,
  bundled BridgeHost launched and closed). **G4/G5 (FX button, MCM) must be confirmed in game.**
