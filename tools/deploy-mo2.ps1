<#
.SYNOPSIS
  Developer helper: updates an installed MO2 copy of the mod from dist\Data (run tools\package.ps1 first).

.DESCRIPTION
  * Refuses to run while Fallout or the BridgeHost is running (DLL/EXE would be locked, and the
    BridgeHost must restore audio routing itself).
  * Keeps the user's MCM settings: Config\MojaveDesktopRadio.ini is never overwritten if it exists.
  * Removes files of earlier versions that are no longer part of the package.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\deploy-mo2.ps1 -ModFolder "D:\MO2\mods\Mojave Desktop Radio"
#>
param([Parameter(Mandatory)] [string] $ModFolder)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$data = Join-Path $root 'dist\Data'
if (-not (Test-Path $data)) { throw 'dist\Data not found - run tools\package.ps1 first' }
if (-not (Test-Path $ModFolder)) { throw "mod folder not found: $ModFolder" }
if (Get-Process -Name 'FalloutNV', 'MojaveDesktopRadio' -ErrorAction SilentlyContinue) {
    throw 'Close Fallout: New Vegas and exit the BridgeHost from the tray first.'
}

$keep = 'Config\MojaveDesktopRadio.ini'
$new = Get-ChildItem $data -Recurse -File | ForEach-Object { $_.FullName.Substring($data.Length + 1) }

# remove stale files from older versions (never the settings file)
Get-ChildItem $ModFolder -Recurse -File | ForEach-Object {
    $rel = $_.FullName.Substring($ModFolder.TrimEnd('\').Length + 1)
    if ($rel -ne $keep -and $rel -ne 'meta.ini' -and $new -notcontains $rel) {
        Write-Host "remove  $rel"
        Remove-Item $_.FullName -Force
    }
}

foreach ($rel in $new) {
    $target = Join-Path $ModFolder $rel
    if ($rel -eq $keep -and (Test-Path $target)) { Write-Host "keep    $rel (your MCM settings)"; continue }
    New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
    Copy-Item (Join-Path $data $rel) $target -Force
}
Write-Host "deployed $($new.Count) files to $ModFolder"
