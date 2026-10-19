<#
.SYNOPSIS
  Builds, checks and packages Mojave Desktop Radio.

.DESCRIPTION
  1. BridgeHost tests + publish (framework-dependent win-x64, needs the .NET 8 Desktop Runtime)
  2. NVSE plugin (32-bit, Visual Studio 2022 / CMake)
  3. MojaveDesktopRadio.esp generated and validated against FalloutNV.esm
  4. Static checks of all scripts / UDFs / MCM JSON / UIO (tools\lint_scripts.py)
  5. dist\MojaveDesktopRadio-<version>.zip  — ONE archive for MO2 / Vortex / manual install.
     The BridgeHost ships inside it (Data\MojaveDesktopRadio\BridgeHost\) and is started by the plugin.

  Nothing on the machine is installed or changed (unless -InstallBridgeHost).

.PARAMETER Configuration   Release (default) or Debug
.PARAMETER InstallBridgeHost   Also copy the BridgeHost to %LOCALAPPDATA%\MojaveDesktopRadio\app (optional fallback location)
.PARAMETER VanillaEsm   FalloutNV.esm for the ESP comparison (default: found through the registry / Steam)

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\package.ps1
#>
param(
    [ValidateSet('Release', 'Debug')] [string] $Configuration = 'Release',
    [switch] $InstallBridgeHost,
    [string] $VanillaEsm = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
$version = ([xml](Get-Content "$root\bridgehost\Directory.Build.props")).Project.PropertyGroup.Version
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

function Step($text) { Write-Host "`n=== $text ===" -ForegroundColor Cyan }
function Run($exe, [string[]] $arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe failed with exit code $LASTEXITCODE" }
}

function Find-FalloutNVEsm {
    foreach ($key in 'HKLM:\SOFTWARE\WOW6432Node\Bethesda Softworks\FalloutNV', 'HKLM:\SOFTWARE\Bethesda Softworks\FalloutNV') {
        $p = (Get-ItemProperty -Path $key -ErrorAction SilentlyContinue).'Installed Path'
        if ($p -and (Test-Path "$p\Data\FalloutNV.esm")) { return "$p\Data\FalloutNV.esm" }
    }
    return $null
}

Write-Host "Mojave Desktop Radio $version ($Configuration)"
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force $dist | Out-Null

Step 'Scripts / MCM / UIO: static checks'
Run python @('-I', "$root\tools\lint_scripts.py", "$root\mod")

Step 'BridgeHost: unit tests'
Run dotnet @('test', "$root\bridgehost\tests\MojaveDesktopRadio.Tests\MojaveDesktopRadio.Tests.csproj", '-c', $Configuration, '--nologo')

Step 'BridgeHost: publish'
$bhOut = Join-Path $dist 'BridgeHost'
foreach ($proj in 'MojaveDesktopRadio.Tray', 'MojaveDesktopRadio.Cli') {
    Run dotnet @('publish', "$root\bridgehost\src\$proj\$proj.csproj", '-c', $Configuration, '-r', 'win-x64',
        '--self-contained', 'false', '-p:DebugType=none', '-o', $bhOut, '--nologo')
}

Step 'NVSE plugin: build (Win32)'
Push-Location "$root\nvse-plugin"
try {
    Run cmake @('--preset', 'win32')
    Run cmake @('--build', '--preset', $Configuration.ToLower())
} finally { Pop-Location }
$dll = "$root\nvse-plugin\build\win32\$Configuration\MojaveDesktopRadio.dll"

Step 'ESP: generate + validate'
Run python @('-I', "$root\tools\build_esp.py", "$root\mod\MojaveDesktopRadio.esp")
if (-not $VanillaEsm) { $VanillaEsm = Find-FalloutNVEsm }
$inspectArgs = @('-I', "$root\tools\inspect_plugin.py", "$root\mod\MojaveDesktopRadio.esp")
if ($VanillaEsm -and (Test-Path $VanillaEsm)) { $inspectArgs += @('--vanilla', $VanillaEsm) }
else { Write-Warning 'FalloutNV.esm not found - skipping the comparison with vanilla (use -VanillaEsm)' }
Run python $inspectArgs

Step 'Assemble Data folder'
$data = Join-Path $dist 'Data'
New-Item -ItemType Directory -Force $data | Out-Null
Copy-Item "$root\mod\*" $data -Recurse -Force
Copy-Item $dll "$data\NVSE\Plugins\MojaveDesktopRadio.dll" -Force
$bhData = "$data\MojaveDesktopRadio\BridgeHost"
New-Item -ItemType Directory -Force $bhData | Out-Null
Copy-Item "$bhOut\*" $bhData -Recurse -Force
New-Item -ItemType Directory -Force "$data\MojaveDesktopRadio\Docs" | Out-Null
foreach ($doc in 'README.md', 'README_TR.md', 'CHANGELOG.md', 'THIRD_PARTY_NOTICES.md', 'LICENSE') {
    if (Test-Path "$root\$doc") { Copy-Item "$root\$doc" "$data\MojaveDesktopRadio\Docs\$doc" -Force }
}

# the plugin INI written by the offline test harness must never ship
$bad = Get-ChildItem $data -Recurse -File | Where-Object { $_.Name -match '\.(bak|tmp|log|pdb)$' }
if ($bad) { throw "unexpected files in the package: $($bad.FullName -join ', ')" }
if ((Get-Content "$data\NVSE\Plugins\MojaveDesktopRadio.ini" -Raw) -match 'iPipboyRadioAddress') { throw 'test INI leaked into the package' }

Step 'Checksums'
# Lets users and mod-site reviewers verify every shipped file against a build from source.
$sums = Get-ChildItem $data -Recurse -File | Sort-Object FullName | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.FullName.Substring($data.Length + 1).Replace('\', '/')
}
$sums | Set-Content "$data\MojaveDesktopRadio\Docs\SHA256SUMS.txt" -Encoding ASCII

Step 'Archive'
$zip = Join-Path $dist "MojaveDesktopRadio-$version.zip"
Compress-Archive -Path "$data\*" -DestinationPath $zip -Force
'{0}  {1}' -f (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower(), (Split-Path $zip -Leaf) | Set-Content "$dist\SHA256SUMS.txt" -Encoding ASCII
$sums | Add-Content "$dist\SHA256SUMS.txt" -Encoding ASCII

if ($InstallBridgeHost) {
    Step 'Install BridgeHost to %LOCALAPPDATA%\MojaveDesktopRadio\app'
    $app = Join-Path $env:LOCALAPPDATA 'MojaveDesktopRadio\app'
    if (Get-Process -Name 'MojaveDesktopRadio' -ErrorAction SilentlyContinue) {
        throw 'MojaveDesktopRadio.exe is running - exit it from the tray (this restores audio routing) and run again.'
    }
    New-Item -ItemType Directory -Force $app | Out-Null
    Copy-Item "$bhOut\*" $app -Recurse -Force
    Write-Host "installed: $app\MojaveDesktopRadio.exe"
}

Step 'Done'
Get-ChildItem $dist -Filter *.zip | ForEach-Object { '{0,-40} {1,12:N0} bytes' -f $_.Name, $_.Length }
