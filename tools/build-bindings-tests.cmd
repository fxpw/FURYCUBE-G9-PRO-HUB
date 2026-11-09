@echo off
cd /d "%~dp0.."
if not exist bin mkdir bin
"%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /platform:x64 /target:library /out:bin\G9Bindings.dll src\G9Bindings.cs
if errorlevel 1 exit /b 1
"%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /platform:x64 /target:exe /out:bin\BindingTests.exe src\G9Bindings.cs tests\BindingTests.cs
if errorlevel 1 exit /b 1
bin\BindingTests.exe
exit /b %errorlevel%
