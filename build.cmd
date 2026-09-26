@echo off
rem Builds BrightnessSync.exe with the C# compiler that ships with .NET Framework 4.5+.
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

"%CSC%" /nologo /target:winexe /optimize ^
  /r:System.Management.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  /win32icon:BrightnessSync.ico /out:BrightnessSync.exe BrightnessSync.cs

exit /b %ERRORLEVEL%
