# Third-party notices

Mojave Desktop Radio redistributes the following components inside `Data\MojaveDesktopRadio\BridgeHost\`.

## NAudio (NAudio.Core, NAudio.Wasapi) 2.2.1

https://github.com/naudio/NAudio — MIT License

```
Copyright 2020 Mark Heath

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute,
sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or
substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT
NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES
OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

## C#/WinRT runtime and Windows SDK projection (WinRT.Runtime.dll, Microsoft.Windows.SDK.NET.dll)

https://github.com/microsoft/CsWinRT — MIT License, Copyright (c) Microsoft Corporation.
(Same MIT terms as above.)

## Acknowledgements (no code redistributed)

* **xNVSE** — plugin interface and event manager (`nvse-plugin/src/nvse_api.h` mirrors the public
  `PluginAPI.h` structure layouts).
* **JIP LN NVSE** — the Pip-Boy radio global (`0x011DD42C` on 1.4.0.525) and the menu-click event used
  for the Pip-Boy buttons.
* **EarTrumpet** — reference for the layout of Windows' internal `IAudioPolicyConfigFactory`
  (per-app audio device), which the BridgeHost declares independently.
* **ShowOff xNVSE, MCM Extender, UIO** — INI cache, JSON MCM and XML injection the mod relies on.
