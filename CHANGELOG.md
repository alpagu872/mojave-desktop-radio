# Changelog

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
