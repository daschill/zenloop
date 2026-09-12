<#
.SYNOPSIS
  Build unsigned Inno Setup and/or MSIX installers from dist\ZenLoop (after publish.ps1).

.DESCRIPTION
  No EV / code-signing certificate is required. Scripts produce unsigned artifacts suitable
  for GitHub Releases. SmartScreen may warn until reputation or a purchased cert exists.

.PARAMETER SkipPublish
  Do not run publish.ps1 (expects dist\ZenLoop already populated).

.PARAMETER Inno
  Compile scripts\installer.iss when ISCC.exe is on PATH or under Program Files.

.PARAMETER Msix
  Stage an MSIX layout and run MakeAppx.exe when the Windows SDK is installed.

.PARAMETER Version
  Override app version (defaults to <Version> from app\ZenLoop.App.csproj).
#>
[CmdletBinding()]
param(
    [switch]$SkipPublish,
    [switch]$Inno = $true,
    [switch]$Msix,
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $Root

$csproj = Join-Path $Root "app\ZenLoop.App.csproj"
if (-not $Version) {
    $versionNode = Select-Xml -Path $csproj -XPath "//Version" | Select-Object -First 1
    $Version = if ($versionNode) { $versionNode.Node.InnerText.Trim() } else { "0.0.0" }
}

$distApp = Join-Path $Root "dist\ZenLoop"
if (-not $SkipPublish) {
    Write-Host "Running publish.ps1..."
    powershell -ExecutionPolicy Bypass -File (Join-Path $Root "publish.ps1")
}

if (-not (Test-Path (Join-Path $distApp "ZenLoop.exe"))) {
    throw "Missing dist\ZenLoop\ZenLoop.exe — run publish.ps1 first or omit -SkipPublish."
}

$built = @()

function Find-Iscc {
    $cmd = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { return $c }
    }
    return $null
}

function Find-MakeAppx {
    $cmd = Get-Command makeappx.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $sdkRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    if (Test-Path $sdkRoot) {
        $hit = Get-ChildItem -Path $sdkRoot -Recurse -Filter makeappx.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    return $null
}

if ($Inno) {
    $iscc = Find-Iscc
    if (-not $iscc) {
        Write-Warning "Inno Setup 6 (ISCC.exe) not found. Install from https://jrsoftware.org/isinfo.php or pass -Inno:`$false."
    }
    else {
        $iss = Join-Path $Root "scripts\installer.iss"
        Write-Host "Compiling Inno script with $iscc (AppVersion=$Version)..."
        & $iscc "/DMyAppVersion=$Version" $iss
        if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit $LASTEXITCODE" }
        $setup = Join-Path $Root "dist\ZenLoop-$Version-Setup.exe"
        if (Test-Path $setup) {
            $built += $setup
            Write-Host "Inno installer: $setup"
        }
    }
}

if ($Msix) {
    $makeappx = Find-MakeAppx
    $stage = Join-Path $Root "dist\msix-stage"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    Copy-Item (Join-Path $distApp "*") $stage -Recurse -Force

    $assets = Join-Path $stage "Assets"
    New-Item -ItemType Directory -Force -Path $assets | Out-Null
    $assetSrc = Join-Path $Root "scripts\msix-assets"
    $names = @("StoreLogo.png", "Square150x150Logo.png", "Square44x44Logo.png", "Wide310x150Logo.png")
    foreach ($name in $names) {
        $src = Join-Path $assetSrc $name
        $dst = Join-Path $assets $name
        if (Test-Path $src) {
            Copy-Item $src $dst -Force
        }
        else {
            Write-Warning "Missing branded asset $src — writing minimal 1x1 PNG fallback."
            $pngBytes = [Convert]::FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==")
            [IO.File]::WriteAllBytes($dst, $pngBytes)
        }
    }

    $manifestSrc = Join-Path $Root "scripts\AppxManifest.xml"
    $manifestDst = Join-Path $stage "AppxManifest.xml"
    $xml = Get-Content $manifestSrc -Raw
    # Identity Version needs four parts.
    $four = if ($Version -match '^\d+\.\d+\.\d+$') { "$Version.0" } else { $Version }
    $xml = [regex]::Replace($xml, 'Version="[^"]+"', "Version=`"$four`"", 1)
    Set-Content -Path $manifestDst -Value $xml -Encoding UTF8

    $msixPath = Join-Path $Root "dist\ZenLoop-$Version-win-x64.msix"
    if (Test-Path $msixPath) { Remove-Item $msixPath -Force }

    if (-not $makeappx) {
        Write-Warning "MakeAppx.exe not found (Windows SDK). Staged layout left at $stage — pack later with MakeAppx pack /d `"$stage`" /p `"$msixPath`" /o"
    }
    else {
        Write-Host "Packing MSIX with $makeappx..."
        & $makeappx pack /d $stage /p $msixPath /o
        if ($LASTEXITCODE -ne 0) { throw "MakeAppx failed with exit $LASTEXITCODE" }
        $built += $msixPath
        Write-Host "MSIX (unsigned): $msixPath"
        Write-Host "Sideload: enable Developer Mode, or sign with your own cert. No EV cert required to build."
    }
}

Write-Host ""
Write-Host "Packaging complete (version $Version)."
if ($built.Count -eq 0) {
    Write-Host "No installer binary produced (missing ISCC/MakeAppx, or flags disabled). Zip from publish.ps1 remains the primary artifact."
}
else {
    $built | ForEach-Object { Write-Host "  $_" }
}
Write-Host "Attach docs\version.json (or version.example.json content) as Release asset 'version.json' for in-app update checks."
