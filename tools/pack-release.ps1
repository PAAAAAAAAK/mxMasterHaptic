<#
.SYNOPSIS
    Builds and packs a dual-platform Thrum Haptics release.

.DESCRIPTION
    One .lplug4 carries both platforms. LoupedeckPackage.yaml names a folder per
    OS - pluginFolderWin: bin, pluginFolderMac: mac - and the Plugin Service loads
    only the one matching the machine it is running on.

    The two folders hold DIFFERENT files, which is why they cannot be merged the
    way Logitech's own plugins merge them. Windows ships uiohook.dll and a WinForms
    executable; macOS ships Avalonia and its Skia renderer and no native binary of
    ours at all. Neither belongs on the other platform.

    Both output trees are deleted before building. This is not superstition: a
    build only ever ADDS to its output directory, so a renamed or removed project
    leaves its old assemblies behind and `pack` ships them. That is invisible in
    the build log and `verify` passes happily, because the package is structurally
    fine - it just contains files it should not. It has happened once already.

.EXAMPLE
    ./tools/pack-release.ps1 -Version 1.2.0
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$plugin = Join-Path $root 'ThrumHapticsPlugin\src\ThrumHapticsPlugin.csproj'
$winOut = Join-Path $root "ThrumHapticsPlugin\bin\$Configuration"
$macOut = Join-Path $root "ThrumHapticsPlugin\bin-mac\$Configuration"
$staging = Join-Path $root "ThrumHapticsPlugin\bin-package"
$dist = Join-Path $root 'dist'

foreach ($d in @((Join-Path $root 'ThrumHapticsPlugin\bin'), (Join-Path $root 'ThrumHapticsPlugin\bin-mac'), $staging)) {
    if (Test-Path -LiteralPath $d) { Remove-Item -LiteralPath $d -Recurse -Force }
}

Write-Host 'Building Windows...' -ForegroundColor Cyan
# IsDevLoopBuild=false so packaging never rewrites the local .link file or asks a
# running Plugin Service to reload mid-build.
dotnet build $plugin -c $Configuration -p:IsDevLoopBuild=false --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw 'Windows build failed' }

Write-Host 'Building macOS (osx-arm64)...' -ForegroundColor Cyan
dotnet build $plugin -c $Configuration -r osx-arm64 --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw 'macOS build failed' }

# The Windows tree is the base because it already holds events/ and metadata/,
# which are platform-neutral and must not be duplicated.
New-Item -ItemType Directory -Path $staging -Force | Out-Null
Copy-Item -Path (Join-Path $winOut '*') -Destination $staging -Recurse -Force

# bin/mac, NESTED, matching pluginFolderMac in the manifest. A top-level 'mac'
# sibling installed fine on Windows and failed on macOS, so the service does not
# appear to resolve an arbitrary folder outside the Windows one.
Copy-Item -Path (Join-Path $macOut 'bin') -Destination (Join-Path $staging 'bin\mac') -Recurse -Force

if (-not (Test-Path -LiteralPath $dist)) { New-Item -ItemType Directory -Path $dist | Out-Null }

$out = Join-Path $dist ("ThrumHaptics_" + ($Version -replace '\.', '_') + '.lplug4')
if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Force }

logiplugintool pack $staging $out
if ($LASTEXITCODE -ne 0) { throw 'pack failed' }

logiplugintool verify $out
if ($LASTEXITCODE -ne 0) { throw 'verify failed' }

# Sanity check rather than trust: the manifest version must match what was asked
# for, and no file from a previous name may have survived into the package.
$manifest = Get-Content (Join-Path $staging 'metadata\LoupedeckPackage.yaml') -Encoding utf8
$declared = ($manifest | Select-String '^version:').Line -replace 'version:\s*', ''

if ($declared.Trim() -ne $Version) {
    throw "LoupedeckPackage.yaml declares $declared but -Version said $Version. Update the manifest."
}

$stale = Get-ChildItem $staging -Recurse -File | Where-Object { $_.Name -match '(?i)mxhaptic' }
if ($stale) { throw "Stale files from the old plugin name: $($stale.Name -join ', ')" }

Write-Host ''
Write-Host "Packed $out" -ForegroundColor Green
Get-Item $out | Select-Object Name, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 2) } }
