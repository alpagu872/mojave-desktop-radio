# Changelog

## 0.2.1

**Fixes**
* From the second save load of a game session on (e.g. after dying), the Pip-Boy panel froze with old
  values, MCM changes were ignored and corner messages stopped, while the buttons still worked.
  Cause: xNVSE removes every handler registered with `SetEventHandlerAlt` when a save is loaded, and the
  load script registered them only once per session. They are now registered on every load
  (`tools/lint_scripts.py` rejects the old pattern).
* The panel is additionally refreshed 4x per second (written only when something changed or the panel
  is stale) and never during a load, so it always matches the real radio state — after dying the game
  switches the radio off, tune Mojave Desktop Radio again.
* The plugin log now states after every load whether the Pip-Boy radio is on our station.
* Requires `MojaveDesktopRadio.dll` v3 (the scripts check this).

## 0.2.0

**Fixes**
* MCM settings and the Pip-Boy **CHANGE FX MODE** button had no effect: `PushConfig.gek` did not
  compile in game (the script compiler truncated an over-long line, then rejected string building).
  Settings are now sent with one short `DispatchEventAlt "MDR:Set" "<key>" <value>` per option.
* Station on/off and setting changes were applied with ~1 s delay: audio endpoint names were re-read
  from Windows on every change (1.5 s on a PC with 9 endpoints). Now cached (0.1 ms).
* After switching the station off, Spotify's lingering audio session was briefly mistaken for a
  manual routing (log: `route: Manual`), starting an unneeded pass-through.
* The BridgeHost could log "game process has exited" twice on shutdown.

**Changes**
* The BridgeHost now ships inside the mod archive (`Data\MojaveDesktopRadio\BridgeHost\`) and is
  started automatically — no separate install.
* Pip-Boy text code page is detected automatically (`iCodePage = 0` → Windows ANSI code page).
* The plugin warns (log + corner message) if the MCM settings script never reports in.
* New `tools/lint_scripts.py`: static checks of all scripts, MCM and UIO files before packaging.
* Requires `MojaveDesktopRadio.dll` v2 (the scripts check this).

## 0.1.0

* First version: Pip-Boy station, Spotify control through Windows media sessions, Pip-Boy panel,
  MCM, four radio presets, automatic audio routing with restore and crash recovery.
