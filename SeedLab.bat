@echo off
rem SeedLab for Windows. Double-click this file for a menu, or give it an action:
rem   SeedLab.bat install, web, stop, status, shell, uninstall, remove-build or help.
rem Add --no-pause when a script calls it, so that it never waits for Enter.
rem The work is done by scripts\windows\seedlab.ps1, and docs\scripts.md explains it.
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\windows\seedlab.ps1" %*
exit /b %ERRORLEVEL%
