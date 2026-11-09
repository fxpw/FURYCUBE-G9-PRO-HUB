@echo off
cd /d "%~dp0.."
if not exist bin mkdir bin
"%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /platform:x64 /target:library /out:bin\G9DpiSettings.dll src\G9DpiSettings.cs
if errorlevel 1 exit /b 1
"%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /platform:x64 /target:exe /main:DpiSettingsTests /out:bin\DpiSettingsTests.exe src\G9DpiSettings.cs tests\DpiSettingsTests.cs
if errorlevel 1 exit /b 1
bin\DpiSettingsTests.exe
exit /b %errorlevel%
