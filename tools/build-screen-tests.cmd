@echo off
setlocal
cd /d "%~dp0.."
if not exist bin mkdir bin
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /platform:x64 /optimize+ /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /out:bin\ScreenTests.exe src\G9Screen.cs tests\ScreenTests.cs
if errorlevel 1 exit /b 1
bin\ScreenTests.exe
exit /b %errorlevel%
