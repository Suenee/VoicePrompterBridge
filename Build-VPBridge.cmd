@echo off
setlocal
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (echo ERROR: Windows C# compiler csc.exe was not found.&exit /b 1)
if not exist "dist\main.js" (echo ERROR: dist\main.js is missing. Run npm run build.&exit /b 1)
"%CSC%" /nologo /target:winexe /optimize+ /win32icon:"assets\VPBridge.ico" /out:"VPBridge.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll "native\VPBridgeTray.cs" "native\BridgeConfig.cs" "native\SettingsForm.cs" "native\LogForm.cs" "native\UiIcons.cs"
if errorlevel 1 exit /b 1
echo BUILD OK: %CD%\VPBridge.exe
