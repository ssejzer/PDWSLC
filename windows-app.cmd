@echo off
setlocal
set "PDWSLC_APP_RUNTIME=win-x64"
if /i "%PROCESSOR_ARCHITECTURE%"=="ARM64" set "PDWSLC_APP_RUNTIME=win-arm64"
if /i "%PROCESSOR_ARCHITEW6432%"=="ARM64" set "PDWSLC_APP_RUNTIME=win-arm64"
set "PDWSLC_APP_EXE=%~dp0artifacts\app\%PDWSLC_APP_RUNTIME%\PDWSLC.exe"
if not exist "%PDWSLC_APP_EXE%" (
    echo Build the Windows app first:
    echo powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-app.ps1" -Runtime %PDWSLC_APP_RUNTIME%
    exit /b 1
)
start "" "%PDWSLC_APP_EXE%"
