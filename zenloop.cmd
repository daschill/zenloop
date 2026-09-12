@echo off
REM DEPRECATED: The supported product is the C# WPF app (ZenLoop.exe / ZenLoop-UI.cmd).
REM This launcher remains for legacy native-helper debugging only. Prefer publish.ps1.
echo.
echo ZenLoop: the Python CLI is deprecated. Use ZenLoop.exe (run publish.ps1 or ZenLoop-UI.cmd).
echo Native helpers still build into zenloop\bin\ for the WPF app — that path is unchanged.
echo.
set "ROOT=%~dp0"
set "PYTHONPATH=%ROOT%;%PYTHONPATH%"
python -m zenloop %*
