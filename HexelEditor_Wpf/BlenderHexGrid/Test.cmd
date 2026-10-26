@echo off
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0Test.ps1" %*
pause
