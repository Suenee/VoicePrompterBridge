@echo off
setlocal EnableExtensions
cd /d "%~dp0"
echo Building Socket Universe Bridge (.NET 10)...
where dotnet >NUL 2>&1 || (echo ERROR: dotnet was not found.& exit /b 1)
dotnet --list-sdks | findstr /B "10." >NUL || (echo ERROR: .NET 10 SDK is required.& exit /b 1)
dotnet publish "native\SocketUniverseBridge.csproj" -c Release -r win-x64 --self-contained false -o "publish"
if errorlevel 1 exit /b 1
copy /Y "publish\SocketUniverseBridge.exe" "SocketUniverseBridge.exe" >NUL
if errorlevel 1 exit /b 1
echo BUILD OK: %CD%\SocketUniverseBridge.exe
exit /b 0
