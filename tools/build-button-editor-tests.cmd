@echo off
setlocal
cd /d "%~dp0.."
if not exist bin mkdir bin
"%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /platform:x64 /main:ButtonEditorTests /out:bin\ButtonEditorTests.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll src\G9Bindings.cs src\G9ButtonEditor.cs tests\ButtonEditorTests.cs
if errorlevel 1 exit /b 1
bin\ButtonEditorTests.exe
exit /b %errorlevel%
