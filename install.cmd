@echo off
setlocal EnableExtensions EnableDelayedExpansion

rem Run the real installer from TEMP so a fresh Git checkout may safely replace install.cmd itself.
if /I "%~1"=="--runner" goto :runner
set "INSTALL_TMP=%TEMP%\sub-install-%RANDOM%-%RANDOM%.cmd"
copy /y "%~f0" "%INSTALL_TMP%" >nul 2>nul
if errorlevel 1 (
    echo ERROR: Could not create temporary installer copy.
    exit /b 1
)
call "%INSTALL_TMP%" --runner "%~dp0" "%~1"
set "INSTALL_RC=%ERRORLEVEL%"
del /q "%INSTALL_TMP%" >nul 2>nul
exit /b %INSTALL_RC%

:runner
cls
title Socket Universe Bridge - installer

set "REPO_URL=https://github.com/Suenee/VoicePrompterBridge.git"
set "BRANCH=devel"
set "ROOT=%~2"
set "INSTALL_OPTION=%~3"
if "%ROOT:~-1%"=="\" set "ROOT=%ROOT:~0,-1%"

rem Allow Git operations on mapped/network drives for this installer and all child processes only.
set "GIT_CONFIG_COUNT=1"
set "GIT_CONFIG_KEY_0=safe.directory"
set "GIT_CONFIG_VALUE_0=*"

pushd "%ROOT%" >nul 2>nul
if errorlevel 1 (
    echo ERROR: Cannot access installer directory: %ROOT%
    goto :fail
)
set "REPO_DIR=%CD%"

if not exist "%REPO_DIR%\logs" mkdir "%REPO_DIR%\logs" >nul 2>nul
set "INSTALL_LOG=%REPO_DIR%\logs\install.log"
>"%INSTALL_LOG%" echo Socket Universe Bridge install started %DATE% %TIME%

echo ============================================
echo Socket Universe Bridge - ONE COMMAND INSTALL
echo ============================================
echo Location: %REPO_DIR%
echo Branch:   %BRANCH%
echo.

call :ensure_powershell || goto :fail
call :ensure_git || goto :fail
call :ensure_node || goto :fail
call :ensure_dotnet10 || goto :fail
call :bootstrap_repo || goto :fail

if not exist "%REPO_DIR%\upgrade.cmd" (
    echo ERROR: upgrade.cmd is missing after repository bootstrap.
    goto :fail
)

echo.
echo [INSTALL] Running current project updater/build pipeline...
call "%REPO_DIR%\upgrade.cmd"
if errorlevel 1 goto :fail

call :verify_install || goto :fail

echo.
echo ============================================
echo INSTALL OK
echo Socket Universe Bridge is ready.
echo EXE: %REPO_DIR%\SocketUniverseBridge.exe
echo CONFIG: %REPO_DIR%\config\vpbridge.json
echo ============================================
>>"%INSTALL_LOG%" echo STATUS: SUCCESS

if /I not "%INSTALL_OPTION%"=="--no-start" (
    echo Starting Socket Universe Bridge...
    start "" /D "%REPO_DIR%" "%REPO_DIR%\SocketUniverseBridge.exe"
)

popd
exit /b 0

:ensure_powershell
where powershell.exe >nul 2>nul
if errorlevel 1 (
    echo ERROR: Windows PowerShell is required.
    exit /b 1
)
exit /b 0

:ensure_winget
where winget.exe >nul 2>nul
if errorlevel 1 (
    echo ERROR: winget is required to install missing prerequisites automatically.
    echo Install Microsoft App Installer and run install.cmd again.
    exit /b 1
)
exit /b 0

:refresh_paths
set "PATH=%ProgramFiles%\Git\cmd;%ProgramFiles%\nodejs;%ProgramFiles%\dotnet;%PATH%"
exit /b 0

:ensure_git
where git.exe >nul 2>nul
if not errorlevel 1 goto :git_ok

echo [PREREQ] Git for Windows is missing. Installing...
call :ensure_winget || exit /b 1
winget install --id Git.Git --exact --accept-package-agreements --accept-source-agreements --silent
if errorlevel 1 (
    echo ERROR: Git installation failed.
    exit /b 1
)
call :refresh_paths
where git.exe >nul 2>nul
if errorlevel 1 (
    echo ERROR: Git was installed but is not available in this process.
    exit /b 1
)
:git_ok
for /f "delims=" %%V in ('git --version') do echo [PREREQ] %%V
exit /b 0

:ensure_node
where node.exe >nul 2>nul
if errorlevel 1 goto :install_node
where npm.cmd >nul 2>nul
if errorlevel 1 goto :install_node

set "NODE_VERSION="
for /f "delims=" %%V in ('node --version') do set "NODE_VERSION=%%V"
set "NODE_MAJOR=%NODE_VERSION:v=%"
for /f "tokens=1 delims=." %%M in ("%NODE_MAJOR%") do set "NODE_MAJOR=%%M"
set /a NODE_MAJOR_NUM=%NODE_MAJOR% 2>nul
if %NODE_MAJOR_NUM% GEQ 22 goto :node_ok

echo [PREREQ] Node.js %NODE_VERSION% is too old. Node.js 22 or newer is required.

:install_node
echo [PREREQ] Installing current Node.js LTS...
call :ensure_winget || exit /b 1
winget install --id OpenJS.NodeJS.LTS --exact --accept-package-agreements --accept-source-agreements --silent
if errorlevel 1 (
    rem If already installed but PATH/version is stale, try an upgrade as well.
    winget upgrade --id OpenJS.NodeJS.LTS --exact --accept-package-agreements --accept-source-agreements --silent
)
call :refresh_paths
where node.exe >nul 2>nul
if errorlevel 1 (
    echo ERROR: Node.js installation completed but node.exe is not available.
    exit /b 1
)
where npm.cmd >nul 2>nul
if errorlevel 1 (
    echo ERROR: Node.js installation completed but npm.cmd is not available.
    exit /b 1
)

:node_ok
for /f "delims=" %%V in ('node --version') do echo [PREREQ] Node %%V
for /f "delims=" %%V in ('npm --version') do echo [PREREQ] npm %%V
exit /b 0

:ensure_dotnet10
where dotnet.exe >nul 2>nul
if errorlevel 1 goto :install_dotnet10
dotnet --list-sdks 2>nul | findstr /B "10." >nul
if not errorlevel 1 goto :dotnet_ok

:install_dotnet10
echo [PREREQ] .NET 10 SDK is missing. Installing...
call :ensure_winget || exit /b 1
winget install --id Microsoft.DotNet.SDK.10 --exact --accept-package-agreements --accept-source-agreements --silent
if errorlevel 1 (
    echo ERROR: .NET 10 SDK installation failed.
    exit /b 1
)
call :refresh_paths
where dotnet.exe >nul 2>nul
if errorlevel 1 (
    echo ERROR: .NET SDK was installed but dotnet.exe is not available.
    exit /b 1
)
dotnet --list-sdks 2>nul | findstr /B "10." >nul
if errorlevel 1 (
    echo ERROR: .NET 10 SDK is still not available after installation.
    exit /b 1
)

:dotnet_ok
for /f "tokens=*" %%V in ('dotnet --list-sdks ^| findstr /B "10."') do echo [PREREQ] .NET SDK %%V
exit /b 0

:bootstrap_repo
if exist "%REPO_DIR%\.git" goto :repo_exists

echo [REPO] No Git working tree found. Bootstrapping DEVEL in this folder...
call :folder_safe_for_bootstrap || exit /b 1

git init
if errorlevel 1 exit /b 1
git remote add origin "%REPO_URL%" >nul 2>nul
git remote set-url origin "%REPO_URL%"
if errorlevel 1 exit /b 1
git fetch origin "%BRANCH%"
if errorlevel 1 exit /b 1
git checkout -f -B "%BRANCH%" "origin/%BRANCH%"
if errorlevel 1 exit /b 1
exit /b 0

:repo_exists
echo [REPO] Existing Git working tree found.
git remote set-url origin "%REPO_URL%" >nul 2>nul
if errorlevel 1 (
    git remote add origin "%REPO_URL%"
    if errorlevel 1 exit /b 1
)
git fetch origin "%BRANCH%"
if errorlevel 1 exit /b 1
exit /b 0

:folder_safe_for_bootstrap
for /f "delims=" %%F in ('dir /b /a "%REPO_DIR%" 2^>nul') do (
    if /I not "%%F"=="install.cmd" if /I not "%%F"=="logs" (
        echo ERROR: Fresh installation folder is not empty.
        echo Unexpected item: %%F
        echo Keep only install.cmd and optional logs folder, then run again.
        exit /b 1
    )
)
exit /b 0

:verify_install
if not exist "%REPO_DIR%\SocketUniverseBridge.exe" (
    echo ERROR: Installation verification failed: SocketUniverseBridge.exe is missing.
    exit /b 1
)
if not exist "%REPO_DIR%\dist\main.js" (
    echo ERROR: Installation verification failed: dist\main.js is missing.
    exit /b 1
)
if not exist "%REPO_DIR%\config\vpbridge.json" (
    echo ERROR: Installation verification failed: config\vpbridge.json is missing.
    exit /b 1
)
echo [VERIFY] SocketUniverseBridge.exe OK
echo [VERIFY] dist\main.js OK
echo [VERIFY] config\vpbridge.json OK
exit /b 0

:fail
set "RC=%ERRORLEVEL%"
if "%RC%"=="0" set "RC=1"
echo.
echo ============================================
echo INSTALL FAILED
echo See: %INSTALL_LOG%
echo ============================================
if defined INSTALL_LOG >>"%INSTALL_LOG%" echo STATUS: FAILED - exit=%RC%
popd >nul 2>nul
pause
exit /b %RC%
