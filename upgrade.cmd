@echo off
cls
setlocal EnableExtensions EnableDelayedExpansion

set "UPGRADE_REV=0.8.0-bootstrap.4"
set "REPO_DIR=%~dp0"
if "!REPO_DIR:~-1!"=="\" set "REPO_DIR=!REPO_DIR:~0,-1!"
cd /d "!REPO_DIR!"
set "SUB_UPGRADE_REPO=!REPO_DIR!"
set "SUB_UPGRADE_BRANCH=devel"
set "SUB_UPGRADE_REMOTE=https://github.com/Suenee/VoicePrompterBridge.git"

rem Git for Windows rejects repositories on some mapped/network drives as dubious ownership.
rem Scope the exception to this updater process and its PowerShell child only; do not change global Git config.
set "GIT_CONFIG_COUNT=1"
set "GIT_CONFIG_KEY_0=safe.directory"
set "GIT_CONFIG_VALUE_0=*"

if not exist "!REPO_DIR!\logs" mkdir "!REPO_DIR!\logs" >nul 2>nul

where git.exe >nul 2>nul
if errorlevel 1 (
    > "!REPO_DIR!\logs\upgrade.log" echo ERROR: Git was not found in PATH.
    >> "!REPO_DIR!\logs\upgrade.log" echo STATUS: FAILED - phase=SELF-UPDATE/BOOTSTRAP
    echo ERROR: Git for Windows is required.
    exit /b 1
)

git rev-parse --is-inside-work-tree >nul 2>nul
if errorlevel 1 (
    > "!REPO_DIR!\logs\upgrade.log" echo ERROR: This folder is not a Git working tree.
    >> "!REPO_DIR!\logs\upgrade.log" echo STATUS: FAILED - phase=SELF-UPDATE/BOOTSTRAP
    echo ERROR: This launcher must be run from the VoicePrompterBridge repository.
    echo For a new computer or empty folder run install.cmd instead.
    exit /b 1
)

git remote set-url origin "!SUB_UPGRADE_REMOTE!" >nul 2>nul
git fetch origin "!SUB_UPGRADE_BRANCH!"
if errorlevel 1 (
    > "!REPO_DIR!\logs\upgrade.log" echo ERROR: git fetch origin failed before PowerShell runner bootstrap.
    >> "!REPO_DIR!\logs\upgrade.log" echo STATUS: FAILED - phase=SELF-UPDATE/BOOTSTRAP
    exit /b 1
)

set "RUNNER_TEMP=%TEMP%\sub-upgrade-%RANDOM%-%RANDOM%.ps1"
git show "origin/!SUB_UPGRADE_BRANCH!:upgrade.ps1" > "!RUNNER_TEMP!" 2>nul
if errorlevel 1 (
    > "!REPO_DIR!\logs\upgrade.log" echo ERROR: Could not extract origin/devel:upgrade.ps1.
    >> "!REPO_DIR!\logs\upgrade.log" echo STATUS: FAILED - phase=SELF-UPDATE/BOOTSTRAP
    echo ERROR: Could not obtain current upgrade.ps1 from origin/!SUB_UPGRADE_BRANCH!.
    del /q "!RUNNER_TEMP!" >nul 2>nul
    exit /b 1
)

rem IMPORTANT: this final block is parsed before PowerShell starts.
rem The runner may update upgrade.cmd on disk without changing commands already parsed here.
(
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "!RUNNER_TEMP!"
    set "UPGRADE_RC=!ERRORLEVEL!"
    del /q "!RUNNER_TEMP!" >nul 2>nul
    exit /b !UPGRADE_RC!
)
