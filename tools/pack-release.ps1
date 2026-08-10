<#
.SYNOPSIS
    Builds and packs one Thrum Haptics release package per platform.

.DESCRIPTION
    TWO packages, one version. Each is single-platform: the manifest keeps only
    the pluginFolder key for its own OS, and its files sit in bin. A Windows user
    downloads 0.4MB instead of 10.7MB, and each file names the platform it is for.

    Each package is built from a COMPLETELY CLEAN TREE, and that is not tidiness -
    it is the fix for a real defect. An earlier version of this script built
    Windows and then macOS in sequence. Because AppendRuntimeIdentifierToOutputPath
    is false (needed so pluginFolderWin can stay 'bin'), the RID is stripped from
    the INTERMEDIATE paths as well, so both builds shared src/obj/Release and the
    second inherited state from the first.

    The macOS package that came out was subtly poisoned: identical file list,
    identical deps.json, an assembly of exactly the same size exporting the same
    APIs - and Logi Options+ refused to install it, every time, with a generic
    "Install plugin method failed". Eight consecutive packages failed that way
    while five built standalone installed first time. Nothing in the package
    content revealed it; only build provenance correlated.

    So: clean, build one platform, pack, clean again, build the other. Never two
    RIDs through one intermediate directory.

    Cleaning also protects the older trap this script was written for. A build only
    ADDS to its output directory, so a renamed or removed project leaves its old
    assemblies behind for pack to ship - invisible in the log, and verify passes
    because the package is structurally fine.

.EXAMPLE
    ./tools/pack-release.ps1 -Version 1.2.0
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'ThrumHapticsPlugin\src\ThrumHapticsPlugin.csproj'
$dist = Join-Path $root 'dist'

$scratch = @(
    'ThrumHapticsPlugin\bin'
    'ThrumHapticsPlugin\bin-mac'
    'ThrumHapticsPlugin\bin-package'
    'ThrumHapticsPlugin\src\obj'
    'ThrumHapticsSettings\bin'
    'ThrumHapticsSettings\obj'
    'ThrumHapticsSettingsMac\bin'
    'ThrumHapticsSettingsMac\obj'
)

function Reset-Tree {
    foreach ($d in $scratch) {
        $p = Join-Path $root $d
        if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force }
    }
}

function New-Package {
    param([string]$Platform, [string]$Rid, [string]$OutputTree, [string]$KeepKey, [string]$DropKey)

    Write-Host "Building $Platform from a clean tree..." -ForegroundColor Cyan
    Reset-Tree

    $args = @($project, '-c', $Configuration, '--nologo', '-v', 'minimal')
    if ($Rid) { $args += @('-r', $Rid) } else { $args += '-p:IsDevLoopBuild=false' }

    # Out-Host, not bare invocation. A PowerShell function returns everything it
    # writes to the pipeline, so build and packer chatter would otherwise be
    # returned alongside the package path.
    dotnet build @args | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$Platform build failed" }

    $staging = Join-Path $root "ThrumHapticsPlugin\$OutputTree\$Configuration"

    # The other platform's key must GO, not merely be ignored. A package declaring
    # support for an OS whose files it does not carry is a broken install waiting
    # to happen.
    $manifest = Join-Path $staging 'metadata\LoupedeckPackage.yaml'
    $text = [IO.File]::ReadAllText($manifest, [Text.Encoding]::UTF8)
    $text = $text.Replace($DropKey, "# omitted: this is a $Platform-only package")
    [IO.File]::WriteAllText($manifest, $text, (New-Object System.Text.UTF8Encoding($false)))

    $declared = (($text -split "`n") | Where-Object { $_ -match '^version:' }) -replace 'version:\s*', ''
    if ($declared.Trim() -ne $Version) {
        throw "LoupedeckPackage.yaml declares '$($declared.Trim())' but -Version said '$Version'."
    }

    if (-not ((Get-Content $manifest -Encoding utf8) -match "^$KeepKey")) {
        throw "$KeepKey missing from the $Platform manifest."
    }

    $stale = Get-ChildItem $staging -Recurse -File | Where-Object { $_.Name -match '(?i)mxhaptic' }
    if ($stale) { throw "Files from the old plugin name survived: $($stale.Name -join ', ')" }

    $out = Join-Path $dist ("ThrumHaptics_" + ($Version -replace '\.', '_') + "_$Platform.lplug4")
    if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Force }

    logiplugintool pack $staging $out | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "pack failed for $Platform" }

    logiplugintool verify $out | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "verify failed for $Platform" }

    return $out
}

if (-not (Test-Path -LiteralPath $dist)) { New-Item -ItemType Directory -Path $dist | Out-Null }

$packages = @(
    (New-Package -Platform 'Windows' -Rid $null -OutputTree 'bin' -KeepKey 'pluginFolderWin' -DropKey 'pluginFolderMac: bin')
    (New-Package -Platform 'macOS' -Rid 'osx-arm64' -OutputTree 'bin-mac' -KeepKey 'pluginFolderMac' -DropKey 'pluginFolderWin: bin')
)

Write-Host ''
Get-Item $packages | Select-Object Name, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 2) } }
