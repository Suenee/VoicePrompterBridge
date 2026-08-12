@echo off
setlocal
cd /d "%~dp0"
if not exist "dist\main.js" (echo ERROR: dist\main.js is missing.&exit /b 1)
node "dist\main.js"
