@echo off
setlocal
cd /d "%~dp0.."
if not exist bin mkdir bin
"%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /platform:x64 /main:MouseSettingsEditorTests /out:bin\MouseSettingsEditorTests.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll src\G9MouseSettings.cs src\G9MouseSettingsEditor.cs src\G9ButtonEditor.cs src\G9ProfileLibrary.cs src\G9Bindings.cs tests\MouseSettingsEditorTests.cs
if errorlevel 1 exit /b 1
bin\MouseSettingsEditorTests.exe
exit /b %errorlevel%
