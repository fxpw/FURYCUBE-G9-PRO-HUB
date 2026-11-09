@echo off
cd /d "%~dp0"
call tools\build-hub.cmd
if errorlevel 1 exit /b 1
call tools\build-screen-tests.cmd
if errorlevel 1 exit /b 1
call tools\build-sensor-tests.cmd
if errorlevel 1 exit /b 1
call tools\build-bindings-tests.cmd
if errorlevel 1 exit /b 1
call tools\build-oled-tests.cmd
if errorlevel 1 exit /b 1
call tools\build-oled-stream-tests.cmd
if errorlevel 1 exit /b 1
call tools\build-oled-worker-tests.cmd
if errorlevel 1 exit /b 1
call tools\build-telemetry-tests.cmd
if errorlevel 1 exit /b 1
call tools\build-button-editor-tests.cmd
if errorlevel 1 exit /b 1
call tools\build-firmware-tests.cmd
exit /b %errorlevel%
