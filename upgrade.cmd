@echo off
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"
set "REPO_URL=https://github.com/Suenee/VoicePrompterBridge.git"
set "BRANCH=devel"
set "WAS_RUNNING=0"
set "AUDIT_FILE=%TEMP%\sub-dotnet-audit-%RANDOM%.txt"

echo ============================================
echo Socket Universe Bridge - DEVEL upgrade
echo ============================================

where git >NUL 2>&1 || (echo ERROR: Git for Windows is required.& goto :fail)
if not exist ".git" (git init & git remote add origin "%REPO_URL%" 2>NUL)
git remote set-url origin "%REPO_URL%" >NUL 2>&1
git diff --quiet || (echo ERROR: Local tracked changes exist. Upgrade stopped before deployment.& goto :fail)
git diff --cached --quiet || (echo ERROR: Local staged changes exist. Upgrade stopped before deployment.& goto :fail)

echo [1/10] Updating upgrade script and source from GitHub...
git fetch origin "%BRANCH%" || goto :fail
git checkout -B "%BRANCH%" "origin/%BRANCH%" || goto :fail
git reset --hard "origin/%BRANCH%" || goto :fail

if not exist "config\vpbridge.json" copy /Y "config\vpbridge.example.json" "config\vpbridge.json" >NUL

echo [2/10] Ensuring .NET 10 SDK...
dotnet --list-sdks 2>NUL | findstr /B "10." >NUL
if errorlevel 1 (
  where winget >NUL 2>&1 || (echo ERROR: winget is required to install .NET 10 SDK.& goto :fail)
  winget install --id Microsoft.DotNet.SDK.10 --exact --accept-package-agreements --accept-source-agreements --silent || goto :fail
  set "PATH=%ProgramFiles%\dotnet;%PATH%"
)

echo [3/10] Installing Node dependencies...
call npm install || goto :fail

echo [4/10] Building SUB server...
if exist dist rmdir /S /Q dist
call npm run build || goto :fail

echo [5/10] Restoring and validating .NET dependencies...
dotnet restore native\SocketUniverseBridge.csproj || goto :fail

echo [6/10] Building .NET 10 tray...
dotnet build native\SocketUniverseBridge.csproj -c Release --no-restore -warnaserror || goto :fail

echo [7/10] Auditing NuGet dependencies...
dotnet list native\SocketUniverseBridge.csproj package --vulnerable --include-transitive > "%AUDIT_FILE%" 2>&1
if errorlevel 1 (type "%AUDIT_FILE%" & echo ERROR: NuGet vulnerability audit failed. & goto :fail)
type "%AUDIT_FILE%"
findstr /I /C:"has the following vulnerable packages" /C:"má následující zranitelné balíčky" /C:"vulnerable package" "%AUDIT_FILE%" >NUL && (echo ERROR: Vulnerable NuGet dependency detected. Upgrade stopped before deployment. & goto :fail)
del /Q "%AUDIT_FILE%" >NUL 2>&1

rem Everything above is non-destructive validation. Only now touch the running installation.
tasklist /FI "IMAGENAME eq VPBridge.Server.exe" 2>NUL | find /I "VPBridge.Server.exe" >NUL && set "WAS_RUNNING=1"
tasklist /FI "IMAGENAME eq SUB.Server.exe" 2>NUL | find /I "SUB.Server.exe" >NUL && set "WAS_RUNNING=1"
tasklist /FI "IMAGENAME eq VPBridge.exe" 2>NUL | find /I "VPBridge.exe" >NUL && set "WAS_RUNNING=1"
tasklist /FI "IMAGENAME eq SocketUniverseBridge.exe" 2>NUL | find /I "SocketUniverseBridge.exe" >NUL && set "WAS_RUNNING=1"

echo [8/10] Stopping bridge and backing up configuration...
taskkill /F /IM VPBridge.exe >NUL 2>&1
taskkill /F /IM VPBridge.Server.exe >NUL 2>&1
taskkill /F /IM SocketUniverseBridge.exe >NUL 2>&1
taskkill /F /IM SUB.Server.exe >NUL 2>&1
if exist "config\vpbridge.json" (
  if not exist "config\migration-backup" mkdir "config\migration-backup"
  copy /Y "config\vpbridge.json" "config\migration-backup\vpbridge-pre-sub.json" >NUL
)

echo [9/10] Publishing SUB and cleaning obsolete runtime...
call Build-VPBridge.cmd || goto :fail_after_stop
if exist "runtime\VPBridge.Server.exe" del /Q "runtime\VPBridge.Server.exe"
if exist "VPBridge.exe" del /Q "VPBridge.exe"
if exist "publish" rmdir /S /Q "publish"
forfiles /P "config\migration-backup" /M "*.json" /D -7 /C "cmd /c del /q @path" >NUL 2>&1

echo Removing obsolete .NET 8 installations...
where winget >NUL 2>&1 && (
  winget uninstall --id Microsoft.DotNet.SDK.8 --exact --silent >NUL 2>&1
  winget uninstall --id Microsoft.DotNet.DesktopRuntime.8 --exact --silent >NUL 2>&1
  winget uninstall --id Microsoft.DotNet.Runtime.8 --exact --silent >NUL 2>&1
  winget uninstall --id Microsoft.DotNet.AspNetCore.8 --exact --silent >NUL 2>&1
)

echo [10/10] Restoring previous running state...
if "%WAS_RUNNING%"=="1" (start "" "%CD%\SocketUniverseBridge.exe") else echo Bridge was stopped before upgrade; leaving SUB stopped.

echo.
echo SUB DEVEL UPGRADE COMPLETED SUCCESSFULLY
exit /b 0

:fail_after_stop
echo ERROR: Deployment failed after the bridge was stopped.
if "%WAS_RUNNING%"=="1" if exist "%CD%\VPBridge.exe" start "" "%CD%\VPBridge.exe"
goto :fail

:fail
if exist "%AUDIT_FILE%" del /Q "%AUDIT_FILE%" >NUL 2>&1
echo.
echo SUB DEVEL UPGRADE FAILED
pause
exit /b 1
