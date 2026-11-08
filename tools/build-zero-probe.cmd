@echo off
cd /d "%~dp0.."
if not exist bin mkdir bin
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:x64 /out:bin\G9ZeroProbe.exe src\G9ZeroProbe.cs
exit /b %errorlevel%
