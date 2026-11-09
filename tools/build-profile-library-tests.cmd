@echo off
cd /d "%~dp0.."
if not exist bin mkdir bin
"%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /platform:x64 /target:exe /define:PROFILE_LIBRARY_TESTS /main:ProfileLibraryTests /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /out:bin\ProfileLibraryTests.exe src\G9ProfileLibrary.cs src\G9Bindings.cs tests\ProfileLibraryTests.cs
if errorlevel 1 exit /b 1
bin\ProfileLibraryTests.exe
exit /b %errorlevel%
