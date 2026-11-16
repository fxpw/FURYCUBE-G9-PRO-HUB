@echo off
cd /d "%~dp0.."
call tools\build-device.cmd
if errorlevel 1 exit /b 1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\build-vue-hub.ps1
exit /b %errorlevel%
