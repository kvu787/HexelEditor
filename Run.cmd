@echo off
pwsh -NoLogo -NoProfile -File "%~dp0Run.ps1" %*
exit /b %errorlevel%
