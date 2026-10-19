# Building

Tools: Visual Studio 2022 (Desktop C++ workload with the **x86** toolset, .NET desktop workload),
CMake 3.21+, .NET SDK 8+, Python 3.10+.

## Everything (recommended)

```powershell
powershell -ExecutionPolicy Bypass -File tools\package.ps1                      # Release
powershell -ExecutionPolicy Bypass -File tools\package.ps1 -Configuration Debug
```

Steps: script lint → BridgeHost tests → BridgeHost publish → plugin build → ESP generation and validation
(FalloutNV.esm found via the registry, or `-VanillaEsm <path>`) → `dist\Data\` → `dist\MojaveDesktopRadio-<version>.zip`.
The version comes from `bridgehost\Directory.Build.props`; keep `nvse-plugin\CMakeLists.txt`,
`kPluginVersionString` in `nvse-plugin\src\main.cpp` and `CHANGELOG.md` in sync.

## Parts

```powershell
# BridgeHost (bridgehost\MojaveDesktopRadio.sln opens in Visual Studio)
dotnet build bridgehost\MojaveDesktopRadio.sln -c Debug
dotnet test  bridgehost\tests\MojaveDesktopRadio.Tests

# NVSE plugin, 32-bit (CMake presets; 64-bit configurations are rejected on purpose)
cd nvse-plugin
cmake --preset win32
cmake --build --preset debug      # build\win32\Debug\MojaveDesktopRadio.dll
cmake --build --preset release
# copy the DLL into a mod folder after every build:
cmake --preset win32 -DMDR_DEPLOY_DIR="<MO2>/mods/Mojave Desktop Radio/NVSE/Plugins"

# ESP
python tools\build_esp.py mod\MojaveDesktopRadio.esp
python tools\inspect_plugin.py mod\MojaveDesktopRadio.esp --vanilla "<game>\Data\FalloutNV.esm"

# scripts / MCM / UIO
python tools\lint_scripts.py mod
```

The plugin links the CRT statically (depends on KERNEL32 only) and is built without the xNVSE source
tree: `nvse-plugin/src/nvse_api.h` mirrors the needed `PluginAPI.h` structures.

## Offline plugin test

`build\win32\Release\mdr_plugin_harness.exe <dll> [seconds] [--autolaunch] [--autoplay]` loads the real DLL
with a fake xNVSE (messages, events) and a fake Pip-Boy radio, simulates load → settings → station on →
preset change → station off → exit, and talks to a running BridgeHost.
It writes a test `MojaveDesktopRadio.ini` next to the DLL it loads — never package that one
(`package.ps1` refuses to).
