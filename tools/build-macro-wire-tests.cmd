@echo off
setlocal
cd /d "%~dp0.."
if not exist bin mkdir bin
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /platform:x64 /optimize+ /reference:System.Web.Extensions.dll /out:bin\MacroWireTests.exe src\G9Macro.cs src\G9MacroWire.cs tests\MacroWireTests.cs
if errorlevel 1 exit /b 1
bin\MacroWireTests.exe
exit /b %errorlevel%
