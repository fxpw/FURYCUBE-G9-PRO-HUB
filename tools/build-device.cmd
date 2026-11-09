@echo off
cd /d "%~dp0.."
if not exist bin mkdir bin
"%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /platform:x64 /target:exe /out:bin\G9Device.exe /reference:System.Web.Extensions.dll src\G9Device.cs src\G9ReadHandshake.cs src\G9SettingsTransactions.cs src\G9MouseSettings.cs src\G9Bindings.cs src\G9Oled.cs src\G9OledRuntimeStatus.cs src\G9OledTransport.cs src\G9OledStreaming.cs
if errorlevel 1 exit /b 1
"%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /platform:x64 /target:exe /main:DeviceProtocolTests /out:bin\DeviceProtocolTests.exe /reference:System.Web.Extensions.dll src\G9Device.cs src\G9ReadHandshake.cs src\G9SettingsTransactions.cs src\G9MouseSettings.cs src\G9Bindings.cs src\G9Oled.cs src\G9OledRuntimeStatus.cs src\G9OledTransport.cs src\G9OledStreaming.cs tests\DeviceProtocolTests.cs
if errorlevel 1 exit /b 1
bin\DeviceProtocolTests.exe
if errorlevel 1 exit /b 1
"%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /platform:x64 /target:exe /main:SettingsTransactionTests /out:bin\SettingsTransactionTests.exe /reference:System.Web.Extensions.dll src\G9Device.cs src\G9ReadHandshake.cs src\G9SettingsTransactions.cs src\G9MouseSettings.cs src\G9Bindings.cs src\G9Oled.cs src\G9OledRuntimeStatus.cs src\G9OledTransport.cs src\G9OledStreaming.cs tests\SettingsTransactionTests.cs
if errorlevel 1 exit /b 1
bin\SettingsTransactionTests.exe
exit /b %errorlevel%
