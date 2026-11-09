@echo off
cd /d "%~dp0.."
if not exist bin mkdir bin
"%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /platform:x64 /target:library /out:bin\G9MouseSettings.dll src\G9MouseSettings.cs
if errorlevel 1 exit /b 1
"%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /platform:x64 /target:exe /main:MouseSettingsTests /out:bin\MouseSettingsTests.exe src\G9MouseSettings.cs tests\MouseSettingsTests.cs
if errorlevel 1 exit /b 1
bin\MouseSettingsTests.exe
exit /b %errorlevel%
