@echo off
setlocal
cd /d "%~dp0.."
if not exist bin mkdir bin
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /platform:x64 /main:InputCheckTests /out:bin\InputCheckTests.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll src\G9InputCheck.cs tests\InputCheckTests.cs
if errorlevel 1 exit /b 1
bin\InputCheckTests.exe
exit /b %errorlevel%
