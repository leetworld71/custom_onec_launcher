@echo off
rem Builds the launcher, the installer and the portable zip into the dist folder.
rem Uses the C# compiler that ships with Windows (.NET Framework 4), nothing to install.
setlocal
set FW=C:\Windows\Microsoft.NET\Framework64\v4.0.30319
cd /d "%~dp0"

"%FW%\csc.exe" /nologo /codepage:65001 /target:winexe /optimize+ /out:1C-Launcher.exe /win32icon:app.ico ^
  /lib:"%FW%\WPF" /r:PresentationFramework.dll /r:PresentationCore.dll /r:WindowsBase.dll /r:System.Xaml.dll ^
  /r:System.Drawing.dll /r:System.Management.dll /r:System.Windows.Forms.dll ^
  Launcher.cs
if errorlevel 1 exit /b 1

if "%1"=="app" exit /b 0

if not exist dist mkdir dist
"%FW%\csc.exe" /nologo /codepage:65001 /target:winexe /optimize+ /out:dist\1C-Launcher-Setup.exe /win32icon:app.ico ^
  /r:System.Drawing.dll /r:System.Windows.Forms.dll /resource:1C-Launcher.exe,1C-Launcher.exe ^
  Setup.cs
if errorlevel 1 exit /b 1

powershell -NoProfile -Command "Compress-Archive -Path '1C-Launcher.exe','README.md' -DestinationPath 'dist\1C-Launcher-portable.zip' -Force"
exit /b %errorlevel%
