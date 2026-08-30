@echo off
setlocal
cd /d "%~dp0"
title QingToolbox - Tauri module packages

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\package-tauri-modules.ps1" -Smoke
if errorlevel 1 (
  echo Failed to build or verify Tauri module packages. >&2
  pause
  exit /b 1
)

echo.
echo Tauri module packages are ready:
echo   %~dp0artifacts\tauri-modules
pause
exit /b 0
