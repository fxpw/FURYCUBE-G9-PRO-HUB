@echo off
setlocal
cd /d "%~dp0.."
if not exist bin mkdir bin
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /platform:x64 /optimize+ /reference:System.Web.Extensions.dll /out:bin\MacroBackupTests.exe src\G9MacroBackup.cs tests\MacroBackupTests.cs
if errorlevel 1 exit /b 1
bin\MacroBackupTests.exe
exit /b %errorlevel%
