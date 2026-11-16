@echo off
setlocal
cd /d "%~dp0.."
if not exist bin mkdir bin
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /platform:x64 /target:winexe /main:G9InputCheckProgram /out:bin\G9InputCheck.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll src\G9InputCheck.cs
exit /b %errorlevel%
