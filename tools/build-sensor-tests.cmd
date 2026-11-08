@echo off
cd /d "%~dp0.."
if not exist bin mkdir bin
"%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /platform:x64 /target:library /out:bin\G9Sensors.dll /reference:System.Web.Extensions.dll src\G9Sensors.cs
if errorlevel 1 exit /b 1
"%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /platform:x64 /target:exe /main:SensorTests /out:bin\SensorTests.exe /reference:System.Web.Extensions.dll src\G9Sensors.cs tests\SensorTests.cs
if errorlevel 1 exit /b 1
bin\SensorTests.exe
if errorlevel 1 exit /b 1
"%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /platform:x64 /target:exe /main:Furycube.G9.Sensors.SensorSampleProgram /out:bin\G9Sensors.exe /reference:System.Web.Extensions.dll src\G9Sensors.cs
exit /b %errorlevel%
