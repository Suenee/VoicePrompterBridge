@echo off
setlocal EnableExtensions
cd /d "%~dp0"

set "REPO_URL=https://github.com/Suenee/VoicePrompterBridge.git"
set "BRANCH=main"
set "WAS_RUNNING=0"

echo ============================================
echo VoicePrompter Bridge - GitHub upgrade
echo ============================================
echo.

tasklist /FI "IMAGENAME eq VPBridge.Server.exe" 2>NUL | find /I "VPBridge.Server.exe" >NUL && set "WAS_RUNNING=1"

echo [1/7] Stopping VPBridge...
taskkill /F /IM VPBridge.exe >NUL 2>&1
taskkill /F /IM VPBridge.Server.exe >NUL 2>&1

where git >NUL 2>&1
if errorlevel 1 (
    echo ERROR: Git for Windows is not installed or git.exe is not in PATH.
    goto :fail
)

if not exist ".git" (
    echo [2/7] Converting this installation to a GitHub working copy...
    git init
    if errorlevel 1 goto :fail
    git remote add origin "%REPO_URL%" 2>NUL
    git remote set-url origin "%REPO_URL%"
    git fetch origin "%BRANCH%"
    if errorlevel 1 goto :fail
    rem reset --hard intentionally replaces old source files that are in the way,
    rem while files ignored by the new repository (config/logs) remain untouched.
    git reset --hard "origin/%BRANCH%"
    if errorlevel 1 goto :fail
    git branch -M "%BRANCH%"
) else (
    echo [2/7] Checking local source tree...
    git remote set-url origin "%REPO_URL%" >NUL 2>&1
    git diff --quiet
    if errorlevel 1 (
        echo ERROR: Local tracked source files contain changes.
        echo Commit/revert them before running upgrade.cmd.
        goto :fail
    )
    git diff --cached --quiet
    if errorlevel 1 (
        echo ERROR: Local staged source changes exist.
        echo Commit/revert them before running upgrade.cmd.
        goto :fail
    )
    echo [3/7] Downloading current source from GitHub...
    git fetch origin "%BRANCH%"
    if errorlevel 1 goto :fail
    git checkout "%BRANCH%" >NUL 2>&1
    if errorlevel 1 git checkout -B "%BRANCH%" "origin/%BRANCH%"
    if errorlevel 1 goto :fail
    git reset --hard "origin/%BRANCH%"
    if errorlevel 1 goto :fail
)

echo [4/7] Removing obsolete untracked files...
rem -f deletes ordinary untracked files; ignored runtime data remains safe.
git clean -fd

if not exist "config\vpbridge.json" (
    echo Creating default runtime configuration...
    copy /Y "config\vpbridge.example.json" "config\vpbridge.json" >NUL
)

if exist "src\websocketServer.ts" del /Q "src\websocketServer.ts"
if exist "dist" rmdir /S /Q "dist"
if exist "runtime\VPBridge.Server.exe" del /Q "runtime\VPBridge.Server.exe"

echo [5/7] Installing/updating dependencies...
call npm install
if errorlevel 1 goto :fail

echo [6/7] Building TypeScript...
call npm run build
if errorlevel 1 goto :fail

echo [7/7] Building native VPBridge.exe...
call Build-VPBridge.cmd
if errorlevel 1 goto :fail

if "%WAS_RUNNING%"=="1" (
    echo Restoring previous running state...
    start "" "%CD%\VPBridge.exe"
) else (
    echo VPBridge was stopped before upgrade; leaving it stopped.
)

echo.
echo ============================================
echo UPGRADE COMPLETED SUCCESSFULLY
echo ============================================
exit /b 0

:fail
echo.
echo ============================================
echo UPGRADE FAILED
echo ============================================
echo VPBridge will not be started automatically.
pause
exit /b 1
