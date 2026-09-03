@echo off
set "ROOT=%~dp0"
set "PYTHONPATH=%ROOT%;%PYTHONPATH%"
python -m zenloop %*
