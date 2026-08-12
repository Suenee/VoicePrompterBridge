@echo off
setlocal EnableExtensions
cd /d "%~dp0"
set "REPO_URL=https://github.com/Suenee/VoicePrompterBridge.git"
set "BRANCH=main"
set "WAS_RUNNING=0"
tasklist /FI "IMAGENAME eq VPBridge.Server.exe" 2>NUL | find /I "VPBridge.Server.exe" >NUL && set "WAS_RUNNING=1"
taskkill /F /IM VPBridge.exe >NUL 2>&1
taskkill /F /IM VPBridge.Server.exe >NUL 2>&1
where git >NUL 2>&1
if errorlevel 1 (echo ERROR: Git for Windows is not installed or git.exe is not in PATH.&goto :fail)
if not exist ".git" (
 git init || goto :fail
 git remote add origin "%REPO_URL%" 2>NUL
 git remote set-url origin "%REPO_URL%"
 git fetch origin "%BRANCH%" || goto :fail
 git checkout -B "%BRANCH%" "origin/%BRANCH%" || goto :fail
) else (
 git remote set-url origin "%REPO_URL%" >NUL 2>&1
 git diff --quiet || (echo ERROR: Local tracked source files contain changes.&goto :fail)
 git diff --cached --quiet || (echo ERROR: Local staged source changes exist.&goto :fail)
 git fetch origin "%BRANCH%" || goto :fail
 git checkout "%BRANCH%" >NUL 2>&1
 git reset --hard "origin/%BRANCH%" || goto :fail
)
git clean -fd
if not exist "config\vpbridge.json" copy /Y "config\vpbridge.example.json" "config\vpbridge.json" >NUL
if exist "src\websocketServer.ts" del /Q "src\websocketServer.ts"
if exist "dist" rmdir /S /Q "dist"
if exist "runtime\VPBridge.Server.exe" del /Q "runtime\VPBridge.Server.exe"
call npm install || goto :fail
call npm run build || goto :fail
call Build-VPBridge.cmd || goto :fail
if "%WAS_RUNNING%"=="1" start "" "%CD%\VPBridge.exe"
echo UPGRADE COMPLETED SUCCESSFULLY
exit /b 0
:fail
echo UPGRADE FAILED
pause
exit /b 1
