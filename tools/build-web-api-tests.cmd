@echo off
setlocal
cd /d "%~dp0.."
if not exist bin mkdir bin
"%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /platform:x64 /main:WebApiTests /out:bin\WebApiTests.exe /reference:System.Web.Extensions.dll src\G9WebApi.cs src\G9Bindings.cs src\G9Macro.cs tests\WebApiTests.cs
if errorlevel 1 exit /b 1
bin\WebApiTests.exe
exit /b %errorlevel%
