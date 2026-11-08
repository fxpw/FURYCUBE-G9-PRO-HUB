@echo off
cd /d "%~dp0"
call tools\build-hub.cmd
if errorlevel 1 exit /b 1
call tools\build-screen-tests.cmd
if errorlevel 1 exit /b 1
call tools\build-sensor-tests.cmd
exit /b %errorlevel%
