@echo off
cd /d "%~dp0.."
if not exist bin mkdir bin
"%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /platform:x64 /target:library /out:bin\G9Telemetry.dll src\G9Telemetry.cs src\G9TelemetryReader.cs
if errorlevel 1 exit /b 1
"%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /platform:x64 /target:exe /main:TelemetryTests /out:bin\TelemetryTests.exe src\G9Telemetry.cs tests\TelemetryTests.cs
if errorlevel 1 exit /b 1
bin\TelemetryTests.exe
if errorlevel 1 exit /b 1
"%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /platform:x64 /target:exe /main:TelemetryReaderTests /out:bin\TelemetryReaderTests.exe src\G9Telemetry.cs src\G9TelemetryReader.cs tests\TelemetryReaderTests.cs
if errorlevel 1 exit /b 1
bin\TelemetryReaderTests.exe
exit /b %errorlevel%
