$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$helper = Join-Path $root "zenloop\bin\zenloop-hw.exe"
if (-not (Test-Path $helper)) {
    Write-Host "Building hardware helper..."
    powershell -ExecutionPolicy Bypass -File (Join-Path $root "native\build.ps1")
}
dotnet build (Join-Path $root "app\ZenLoop.App.csproj") -c Release --nologo -v q
$exe = Join-Path $root "app\bin\Release\net10.0-windows\ZenLoop.exe"
Start-Process $exe -ArgumentList $args
