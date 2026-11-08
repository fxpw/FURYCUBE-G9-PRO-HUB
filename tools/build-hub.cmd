@echo off
cd /d "%~dp0.."
if not exist bin mkdir bin
call tools\build-device.cmd
if errorlevel 1 exit /b 1
"%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /platform:x64 /target:winexe /main:G9Hub /out:bin\FURYCUBE-G9-PRO-HUB.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll src\G9Hub.cs src\G9ScreenEditor.cs src\G9Screen.cs src\G9Sensors.cs
exit /b %errorlevel%
