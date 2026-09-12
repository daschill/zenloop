<#
.SYNOPSIS
  Optionally Authenticode-sign ZenLoop publish artifacts when SIGNING_CERT_* secrets exist.

.DESCRIPTION
  Unsigned path is unchanged when cert secrets are absent (exit 0, no SignTool).
  When a PFX is available, signs ZenLoop.exe, native helpers, and optional
  Setup.exe / MSIX with SignTool + RFC3161 timestamp.

.PARAMETER DistDir
  Folder containing ZenLoop.exe (default: dist\ZenLoop).

.PARAMETER ExtraFiles
  Additional files to sign (Setup.exe, .msix, etc.).

.PARAMETER DryRun
  Print what would be signed without invoking SignTool.
#>
[CmdletBinding()]
param(
    [string]$DistDir = "",
    [string[]]$ExtraFiles = @(),
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $DistDir) { $DistDir = Join-Path $Root "dist\ZenLoop" }

function Get-SigningMaterial {
    $certPath = $env:SIGNING_CERT_PATH
    $pfxB64 = $env:SIGNING_CERT_PFX
    $password = $env:SIGNING_CERT_PASSWORD
    if (-not $password) { $password = $env:SIGNING_CERT_PWD }

    if ($certPath -and (Test-Path $certPath)) {
        return [pscustomobject]@{ PfxPath = (Resolve-Path $certPath).Path; Password = $password; Temp = $false }
    }

    if ($pfxB64) {
        $tmp = Join-Path $env:TEMP ("zenloop-sign-" + [guid]::NewGuid().ToString("n") + ".pfx")
        [IO.File]::WriteAllBytes($tmp, [Convert]::FromBase64String($pfxB64.Trim()))
        return [pscustomobject]@{ PfxPath = $tmp; Password = $password; Temp = $true }
    }

    return $null
}

function Find-SignTool {
    $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $sdkRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    if (Test-Path $sdkRoot) {
        $hit = Get-ChildItem -Path $sdkRoot -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    return $null
}

$material = Get-SigningMaterial
if (-not $material) {
    Write-Host "SIGNING_CERT_* not set - leaving artifacts unsigned (expected for open builds)."
    Write-Host "See docs/CODE-SIGNING.md for EV certificate setup."
    exit 0
}

if (-not $material.Password) {
    throw "SIGNING_CERT_PFX or SIGNING_CERT_PATH is set but SIGNING_CERT_PASSWORD is missing."
}

$signtool = Find-SignTool
if (-not $signtool -and -not $DryRun) {
    throw "SignTool.exe not found. Install Windows SDK 10 Signing Tools, or omit SIGNING_CERT_* for unsigned builds."
}

$timestamp = $env:SIGNING_TIMESTAMP_URL
if (-not $timestamp) { $timestamp = "http://timestamp.digicert.com" }

$targets = New-Object System.Collections.Generic.List[string]
if (Test-Path $DistDir) {
    foreach ($name in @("ZenLoop.exe", "zenloop-hw.exe", "zenloop-cpu.exe")) {
        $p = Join-Path $DistDir $name
        if (Test-Path $p) { [void]$targets.Add($p) }
    }
}
foreach ($f in $ExtraFiles) {
    if ($f -and (Test-Path $f)) { [void]$targets.Add((Resolve-Path $f).Path) }
}

if ($targets.Count -eq 0) {
    Write-Warning "No files to sign under $DistDir (and no ExtraFiles)."
    if ($material.Temp) { Remove-Item $material.PfxPath -Force -ErrorAction SilentlyContinue }
    exit 0
}

Write-Host "Signing $($targets.Count) file(s) with $($material.PfxPath) (timestamp $timestamp)..."
try {
    foreach ($file in $targets) {
        if ($DryRun) {
            Write-Host "DRY-RUN would sign: $file"
            continue
        }
        Write-Host "  SignTool: $file"
        & $signtool sign /fd SHA256 /td SHA256 /tr $timestamp /f $material.PfxPath /p $material.Password $file
        if ($LASTEXITCODE -ne 0) { throw "SignTool failed ($LASTEXITCODE) for $file" }
    }
    if (-not $DryRun) {
        Write-Host "Authenticode signing complete."
    }
}
finally {
    if ($material.Temp -and (Test-Path $material.PfxPath)) {
        Remove-Item $material.PfxPath -Force -ErrorAction SilentlyContinue
    }
}
