@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Files\Start-GroupChat.ps1" -Background %*
set "result=%ERRORLEVEL%"
if not "%result%"=="0" pause
exit /b %result%
