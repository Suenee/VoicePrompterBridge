@echo off
setlocal EnableExtensions
cd /d "%~dp0"

echo VoicePrompter Bridge v0.6.6 - native tray build
echo.

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

if not exist "%CSC%" (
  echo ERROR: Windows C# compiler csc.exe was not found.
  echo Expected .NET Framework 4.x, normally available on Windows 10/11.
  exit /b 1
)

if not exist "dist\main.js" (
  echo ERROR: dist\main.js is missing.
  echo Run: npm run build
  exit /b 1
)

echo Building VPBridge.exe...
"%CSC%" /nologo /target:winexe /optimize+ /out:"VPBridge.exe" ^
  /reference:System.dll ^
  /reference:System.Core.dll ^
  /reference:System.Drawing.dll ^
  /reference:System.Windows.Forms.dll ^
  /reference:System.Web.Extensions.dll ^
  "native\VPBridgeTray.cs" ^
  "native\BridgeConfig.cs" ^
  "native\SettingsForm.cs" ^
  "native\LogForm.cs" ^
  "native\UiIcons.cs"

if errorlevel 1 (
  echo BUILD FAILED.
  exit /b 1
)

echo BUILD OK: %CD%\VPBridge.exe
exit /b 0
