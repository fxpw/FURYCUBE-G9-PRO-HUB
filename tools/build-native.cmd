@echo off
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvarsall.bat" x86 >nul
if errorlevel 1 exit /b 1
cd /d "%~dp0.."
cl /nologo /EHsc /MT /W4 /Fe:bin\NativeTransportProbe.exe /Fo:bin\NativeTransportProbe.obj src\NativeTransportProbe.cpp
if errorlevel 1 exit /b 1
cl /nologo /EHsc /MT /W4 /Fe:bin\G9Protocol.exe /Fo:bin\G9Protocol.obj src\G9Protocol.cpp
if errorlevel 1 exit /b 1
cl /nologo /EHsc /MT /W4 /Fe:bin\KeymapTests.exe /Fo:bin\KeymapTests.obj tests\KeymapTests.cpp
if errorlevel 1 exit /b 1
bin\KeymapTests.exe
if errorlevel 1 exit /b 1
cl /nologo /EHsc /MT /W4 /LD /Fe:bin\hiddriver_1.dll /Fo:bin\HidTrace.obj src\HidTrace.cpp /link /DEF:src\HidTrace.def /IMPLIB:bin\HidTrace.lib
if errorlevel 1 exit /b 1
cl /nologo /EHsc /MT /W4 /LD /Fe:bin\hidapi.dll /Fo:bin\HidApiTrace.obj src\HidApiTrace.cpp /link /DEF:src\HidApiTrace.def /IMPLIB:bin\HidApiTrace.lib
exit /b %errorlevel%
