@echo off
cd /d "%~dp0"
if not exist bin\FURYCUBE-G9-PRO-HUB.exe call tools\build-hub.cmd
if errorlevel 1 exit /b 1
start "" "%~dp0bin\FURYCUBE-G9-PRO-HUB.exe"
