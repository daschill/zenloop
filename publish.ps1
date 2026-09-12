$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $Root

$helper = Join-Path $Root "zenloop\bin\zenloop-hw.exe"
$cpu = Join-Path $Root "zenloop\bin\zenloop-cpu.exe"
if (-not (Test-Path $helper) -or -not (Test-Path $cpu)) {
    Write-Host "Building native helpers..."
    powershell -ExecutionPolicy Bypass -File (Join-Path $Root "native\build.ps1")
}

$out = Join-Path $Root "dist\ZenLoop"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force -Path $out | Out-Null

dotnet publish (Join-Path $Root "app\ZenLoop.App.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishReadyToRun=true `
    -p:DebugType=none `
    -p:PublishTrimmed=false `
    -o $out `
    --nologo

foreach ($name in @("zenloop-hw.exe", "zenloop-cpu.exe")) {
    $src = Join-Path $Root "zenloop\bin\$name"
    if (Test-Path $src) {
        Copy-Item $src (Join-Path $out $name) -Force
    }
}

$readme = @"
ZenLoop
=======

Double-click ZenLoop.exe. Approve Administrator once (UAC).

Click **Optimize this PC**. The app restores stock GPU, benchmarks, undervolts/overclocks the GPU, tunes per-core Curve Optimizer, benchmarks again, and shows faster / cooler / less power.

Needs AMD Software (Adrenalin) for the GPU. Needs AMD Ryzen Master installed for CPU/BIOS (same signed driver ClockTuner/Hydra use). No HWiNFO or Python required — the supported product is ZenLoop.exe (WPF).
"@
Set-Content -Path (Join-Path $out "README.txt") -Value $readme -Encoding UTF8

Write-Host "Published $out"
Get-ChildItem $out -Name | Select-Object -First 20
$exe = Get-Item (Join-Path $out "ZenLoop.exe")
Write-Host ("ZenLoop.exe {0} bytes" -f $exe.Length)
