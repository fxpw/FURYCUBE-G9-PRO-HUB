@echo off
cd /d "%~dp0.."
if not exist bin mkdir bin
"%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /out:bin\OledRadioTests.exe src\G9Oled.cs src\G9OledRuntimeStatus.cs src\G9OledTransport.cs tests\OledRadioTests.cs
if errorlevel 1 exit /b 1
bin\OledRadioTests.exe
exit /b %errorlevel%
