@echo off
setlocal EnableExtensions EnableDelayedExpansion
for /f "tokens=2 delims=," %%P in ('tasklist /FI "IMAGENAME eq VPBridge.exe" /FO CSV /NH 2^>nul') do taskkill /PID %%~P /T /F >nul 2>&1
for /f "tokens=2 delims=," %%P in ('tasklist /FI "IMAGENAME eq VPBridge.Server.exe" /FO CSV /NH 2^>nul') do taskkill /PID %%~P /F >nul 2>&1
echo Done. No generic node.exe process was touched.
