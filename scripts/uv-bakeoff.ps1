<#
.SYNOPSIS
  Adrenalin UV bake-off: stock vs ZenLoop UV vs optional Adrenalin profile → local report.

.DESCRIPTION
  Offline mode builds the report from saved bench JSON (no GPU). Live mode uses zenloop-hw.exe
  for reset/apply when present, then expects bench JSON paths (or runs OfflineOnly after you bench in UI).

  See docs/UV-BAKEOFF.md
#>
[CmdletBinding()]
param(
    [string]$StockBench = "",
    [string]$ZenLoopBench = "",
    [string]$AdrenalinBench = "",
    [string]$ZenLoopProfile = "",
    [string]$AdrenalinProfile = "",
    [string]$OutDir = "",
    [string]$Goal = "balanced",
    [int]$BenchSeconds = 20,
    [switch]$OfflineOnly,
    [switch]$SkipAdrenalinLeg
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) {
    $OutDir = Join-Path $env:LOCALAPPDATA "ZenLoop\bakeoff"
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function Find-HwHelper {
    $candidates = @(
        (Join-Path $Root "zenloop\bin\zenloop-hw.exe"),
        (Join-Path $Root "dist\ZenLoop\zenloop-hw.exe"),
        (Join-Path $PSScriptRoot "..\zenloop\bin\zenloop-hw.exe")
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { return (Resolve-Path $c).Path }
    }
    return $null
}

function Invoke-Hw([string]$Hw, [string[]]$HwArgs) {
    & $Hw @HwArgs
    if ($LASTEXITCODE -ne 0) { throw "zenloop-hw failed: $HwArgs (exit $LASTEXITCODE)" }
}

$hw = Find-HwHelper

if (-not $OfflineOnly -and $hw) {
    Write-Host "Live prep via $hw (reset / apply). Benches still come from ZenLoop UI paths or -StockBench/-ZenLoopBench."
    Write-Host "1) Restoring stock GPU (Adrenalin Default path)…"
    Invoke-Hw $hw @("reset")

    if (-not $ZenLoopProfile) {
        $defaultProf = Join-Path $env:LOCALAPPDATA "ZenLoop\profiles\startup-gpu.json"
        if (Test-Path $defaultProf) { $ZenLoopProfile = $defaultProf }
    }

    if ($ZenLoopProfile -and (Test-Path $ZenLoopProfile)) {
        Write-Host "2) ZenLoop profile present: $ZenLoopProfile"
        Write-Host "   Apply it from ZenLoop (Restore last tune) or re-run Optimize, then Bench current."
    }

    if ($AdrenalinProfile -and -not $SkipAdrenalinLeg) {
        Write-Host "3) Adrenalin profile to import: $AdrenalinProfile"
        Write-Host "   Host will map voltage/clock fields when building the report."
    }

    Write-Host ""
    Write-Host "Capture benches in ZenLoop (Bench baseline = stock, Bench current = ZenLoop UV),"
    Write-Host "optionally a third JSON for the Adrenalin leg, then re-run with -OfflineOnly and paths."
    Write-Host "Default bench paths under %LOCALAPPDATA%\ZenLoop\profiles\"
}

if (-not $StockBench) {
    $StockBench = Join-Path $env:LOCALAPPDATA "ZenLoop\profiles\bench-baseline.json"
}
if (-not $ZenLoopBench) {
    $ZenLoopBench = Join-Path $env:LOCALAPPDATA "ZenLoop\profiles\bench-current.json"
}

if (-not (Test-Path $StockBench) -or -not (Test-Path $ZenLoopBench)) {
    Write-Host "Missing bench JSON. Need:"
    Write-Host "  stock:    $StockBench"
    Write-Host "  zenloop:  $ZenLoopBench"
    Write-Host "Run ZenLoop Optimize / Bench buttons first, or pass -StockBench/-ZenLoopBench."
    if (-not $OfflineOnly) { exit 0 }
    exit 1
}

$hostProj = Join-Path $PSScriptRoot "bakeoff-host\ZenLoop.BakeOffHost.csproj"
$dotnetArgs = @(
    "run", "--project", $hostProj, "-c", "Release", "--no-launch-profile", "--",
    "build",
    "--stock", $StockBench,
    "--zenloop", $ZenLoopBench,
    "--goal", $Goal,
    "--out", $OutDir
)
if ($AdrenalinBench -and (Test-Path $AdrenalinBench) -and -not $SkipAdrenalinLeg) {
    $dotnetArgs += @("--adrenalin", $AdrenalinBench)
}
if ($ZenLoopProfile -and (Test-Path $ZenLoopProfile)) {
    $dotnetArgs += @("--zenloop-profile", $ZenLoopProfile)
}
if ($AdrenalinProfile -and (Test-Path $AdrenalinProfile) -and -not $SkipAdrenalinLeg) {
    $dotnetArgs += @("--adrenalin-profile", $AdrenalinProfile)
}

Write-Host "Building bake-off report…"
& dotnet @dotnetArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Done. Report directory: $OutDir"
