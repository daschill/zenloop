$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $Root

$Adlx = Join-Path $Root "vendor\ADLX"
if (-not (Test-Path (Join-Path $Adlx "SDK\ADLXHelper\Windows\Cpp\ADLXHelper.cpp"))) {
    New-Item -ItemType Directory -Force -Path (Join-Path $Root "vendor") | Out-Null
    git clone --depth 1 https://github.com/GPUOpen-LibrariesAndSDKs/ADLX.git $Adlx
}

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "Visual Studio vswhere.exe not found" }
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw "MSVC C++ toolset not found. Install Visual Studio Desktop C++ workload." }
$vcvars = Join-Path $vs "VC\Auxiliary\Build\vcvars64.bat"
if (-not (Test-Path $vcvars)) { throw "vcvars64.bat not found at $vcvars" }

$OutDir = Join-Path $Root "zenloop\bin"
$Native = Join-Path $Root "native"
$Obj = Join-Path $Native "build"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
New-Item -ItemType Directory -Force -Path $Obj | Out-Null

$bat = Join-Path $Obj "compile.bat"
$helperCpp = Join-Path $Adlx "SDK\ADLXHelper\Windows\Cpp\ADLXHelper.cpp"
$winApis = Join-Path $Adlx "SDK\Platform\Windows\WinAPIs.cpp"
$exe = Join-Path $OutDir "zenloop-hw.exe"
$cpuExe = Join-Path $OutDir "zenloop-cpu.exe"

@(
    "@echo off"
    "call `"$vcvars`""
    "if errorlevel 1 exit /b 1"
    "cd /d `"$Native`""
    "cl /nologo /c /O2 /EHsc /std:c++17 /DUNICODE /D_UNICODE /DWIN32_LEAN_AND_MEAN /I`"$Adlx`" /Fo`"$Obj\zenloop_hw.obj`" zenloop_hw.cpp"
    "if errorlevel 1 exit /b 1"
    "cl /nologo /c /O2 /EHsc /std:c++17 /DUNICODE /D_UNICODE /DWIN32_LEAN_AND_MEAN /I`"$Adlx`" /Fo`"$Obj\ADLXHelper.obj`" `"$helperCpp`""
    "if errorlevel 1 exit /b 1"
    "cl /nologo /c /O2 /EHsc /std:c++17 /DUNICODE /D_UNICODE /DWIN32_LEAN_AND_MEAN /I`"$Adlx`" /Fo`"$Obj\WinAPIs.obj`" `"$winApis`""
    "if errorlevel 1 exit /b 1"
    "link /nologo /OUT:`"$exe`" `"$Obj\zenloop_hw.obj`" `"$Obj\ADLXHelper.obj`" `"$Obj\WinAPIs.obj`" Advapi32.lib"
    "if errorlevel 1 exit /b 1"
    "cl /nologo /c /O2 /EHsc /std:c++17 /DUNICODE /D_UNICODE /DWIN32_LEAN_AND_MEAN /Fo`"$Obj\zenloop_cpu.obj`" zenloop_cpu.cpp"
    "if errorlevel 1 exit /b 1"
    "link /nologo /OUT:`"$cpuExe`" `"$Obj\zenloop_cpu.obj`" Advapi32.lib"
    "exit /b %ERRORLEVEL%"
) | Set-Content -Path $bat -Encoding ASCII

Write-Host "Running $bat"
cmd.exe /c "`"$bat`""
if ($LASTEXITCODE -ne 0) { throw "compile failed: $LASTEXITCODE" }
if (-not (Test-Path $exe)) { throw "compile reported success but $exe is missing" }
if (-not (Test-Path $cpuExe)) { throw "compile reported success but $cpuExe is missing" }
Write-Host "Built $exe  ($((Get-Item $exe).Length) bytes)"
Write-Host "Built $cpuExe  ($((Get-Item $cpuExe).Length) bytes)"
