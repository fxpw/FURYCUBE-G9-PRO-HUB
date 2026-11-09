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
call tools\build-oled-status-tests.cmd
if errorlevel 1 exit /b 1
call tools\build-oled-stream-tests.cmd
if errorlevel 1 exit /b 1
call tools\build-oled-worker-tests.cmd
if errorlevel 1 exit /b 1
call tools\build-telemetry-tests.cmd
if errorlevel 1 exit /b 1
call tools\build-dpi-tests.cmd
if errorlevel 1 exit /b 1
call tools\build-button-editor-tests.cmd
if errorlevel 1 exit /b 1
call tools\build-mouse-settings-tests.cmd
if errorlevel 1 exit /b 1
call tools\build-read-handshake-tests.cmd
if errorlevel 1 exit /b 1
call tools\build-mouse-settings-editor-tests.cmd
if errorlevel 1 exit /b 1
call tools\build-profile-library-tests.cmd
if errorlevel 1 exit /b 1
call tools\build-macro-tests.cmd
if errorlevel 1 exit /b 1
call tools\build-firmware-tests.cmd
if errorlevel 1 exit /b 1
call tools\build-firmware-ol3-tests.cmd
exit /b %errorlevel%
