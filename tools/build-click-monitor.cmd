@echo off
cd /d "%~dp0.."
if not exist bin mkdir bin
"%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /platform:x64 /target:winexe /out:bin\G9ClickMonitor.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll src\G9ClickMonitor.cs src\G9ClickSignal.cs
if errorlevel 1 exit /b 1
"%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /platform:x64 /out:bin\ClickSignalTests.exe src\G9ClickSignal.cs tests\ClickSignalTests.cs
if errorlevel 1 exit /b 1
bin\ClickSignalTests.exe
exit /b %errorlevel%
