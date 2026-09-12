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

$csproj = Join-Path $Root "app\ZenLoop.App.csproj"
$versionNode = Select-Xml -Path $csproj -XPath "//Version" | Select-Object -First 1
$version = if ($versionNode) { $versionNode.Node.InnerText.Trim() } else { "0.0.0" }

dotnet publish $csproj `
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

foreach ($name in @("LICENSE", "EULA.txt", "DISCLAIMER.txt")) {
    $src = Join-Path $Root $name
    if (Test-Path $src) {
        Copy-Item $src (Join-Path $out $name) -Force
    }
}

$recovery = Join-Path $Root "docs\RECOVERY.md"
if (Test-Path $recovery) {
    Copy-Item $recovery (Join-Path $out "RECOVERY.md") -Force
}
foreach ($doc in @("METRICS-EXPORT.md", "RTSS-OSD.md", "UV-BAKEOFF.md")) {
    $src = Join-Path $Root "docs\$doc"
    if (Test-Path $src) {
        Copy-Item $src (Join-Path $out $doc) -Force
    }
}

$readme = @"
ZenLoop $version
===============

Double-click ZenLoop.exe. Approve Administrator once (UAC).

First launch: read and accept the safety / EULA prompt before Optimize or BIOS writes.
Full text: EULA.txt and DISCLAIMER.txt in this folder.

Click **Optimize this PC**. The app restores stock GPU, benchmarks, undervolts/overclocks the GPU,
tunes per-core Curve Optimizer, benchmarks again, and shows faster / cooler / less power.

Export / Import tune pack saves GPU + CPU + RAM profiles as one JSON backup.

Needs AMD Software (Adrenalin) for the GPU. Needs AMD Ryzen Master installed for CPU/BIOS
(same signed driver path as other AMD Windows tuners). No HWiNFO or Python required.

Not affiliated with AMD. Overclocking can damage hardware - use at your own risk.
Intel/NVIDIA: detect + multi-vendor capability matrix; Apply only when a public signed API
resolves (e.g. NVAPI power policies). No WinRing0 / raw SMU. Never fake Apply success.

Code signing: unsigned by default. If SIGNING_CERT_* secrets are set, publish.ps1 runs
scripts\sign-artifacts.ps1 (see docs\CODE-SIGNING.md). SmartScreen may warn on unsigned builds.

Recovery: see RECOVERY.md (Adrenalin Default, clear CO, CLR_CMOS). About -> No opens the guide.
RTSS OSD: RTSS-OSD.md. Metrics JSON: METRICS-EXPORT.md. UV bake-off: UV-BAKEOFF.md.
Optional installers: scripts\pack-installer.ps1 (Inno / MSIX). Update check: About dialog.
"@
Set-Content -Path (Join-Path $out "README.txt") -Value $readme -Encoding UTF8

$zip = Join-Path $Root "dist\ZenLoop-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip -Force

# Optional Authenticode - no-op when SIGNING_CERT_* absent (unsigned path unchanged).
$signScript = Join-Path $Root "scripts\sign-artifacts.ps1"
if (Test-Path $signScript) {
    powershell -ExecutionPolicy Bypass -File $signScript -DistDir $out
    if ($LASTEXITCODE -ne 0) { throw "sign-artifacts.ps1 failed with exit $LASTEXITCODE" }
    # Re-zip after signing so the zip contains signed binaries.
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip -Force
}

Write-Host "Published $out"
Write-Host "Zip       $zip"
Get-ChildItem $out -Name | Select-Object -First 24
$exe = Get-Item (Join-Path $out "ZenLoop.exe")
Write-Host ("ZenLoop.exe {0} bytes  version {1}" -f $exe.Length, $version)
