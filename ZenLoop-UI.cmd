@echo off
set "ROOT=%~dp0"
set "EXE=%ROOT%app\bin\Release\net10.0-windows\ZenLoop.exe"
if not exist "%EXE%" (
  echo Building ZenLoop UI...
  powershell -ExecutionPolicy Bypass -File "%ROOT%run-ui.ps1"
  exit /b %ERRORLEVEL%
)
start "" "%EXE%" %*
