@echo off
cls
setlocal EnableExtensions
cd /d "%~dp0"
set "SUB_UPGRADE_REPO=%CD%"
set "SUB_UPGRADE_BRANCH=devel"
set "SUB_UPGRADE_REMOTE=https://github.com/Suenee/VoicePrompterBridge.git"
set "SUB_UPGRADE_TEMP=%TEMP%\sub-upgrade-%RANDOM%-%RANDOM%.ps1"

where git >NUL 2>&1
if errorlevel 1 (
  echo ERROR: Git for Windows is required.
  exit /b 1
)

if not exist ".git" (
  echo ERROR: This launcher must be run from the VoicePrompterBridge repository.
  exit /b 1
)

git diff --quiet
if errorlevel 1 (
  echo ERROR: Local tracked changes exist. Upgrade aborted.
  exit /b 1
)
git diff --cached --quiet
if errorlevel 1 (
  echo ERROR: Local staged changes exist. Upgrade aborted.
  exit /b 1
)

git remote set-url origin "%SUB_UPGRADE_REMOTE%" >NUL 2>&1
git fetch origin "%SUB_UPGRADE_BRANCH%"
if errorlevel 1 exit /b %ERRORLEVEL%

git show "origin/%SUB_UPGRADE_BRANCH%:upgrade.ps1" > "%SUB_UPGRADE_TEMP%"
if errorlevel 1 (
  echo ERROR: Could not obtain current upgrade.ps1 from origin/%SUB_UPGRADE_BRANCH%.
  del /q "%SUB_UPGRADE_TEMP%" >NUL 2>&1
  exit /b 1
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SUB_UPGRADE_TEMP%"
set "RC=%ERRORLEVEL%"
del /q "%SUB_UPGRADE_TEMP%" >NUL 2>&1
exit /b %RC%
