@echo off
setlocal
cd /d "%~dp0.."
if not exist bin mkdir bin
"%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /platform:x64 /reference:System.Web.Extensions.dll /main:StartupTests /out:bin\StartupTests.exe src\G9Startup.cs tests\StartupTests.cs
if errorlevel 1 exit /b 1
bin\StartupTests.exe
exit /b %errorlevel%
