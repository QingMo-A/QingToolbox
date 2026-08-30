@echo off
setlocal
cd /d "%~dp0"
title QingToolbox - Tauri installer candidate

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build-tauri-installer.ps1" -Smoke
if errorlevel 1 (
  echo Failed to build or smoke-test the Tauri installer candidate. >&2
  pause
  exit /b 1
)

echo.
echo Tauri installer candidate is ready:
echo   %~dp0artifacts\tauri-installer\output
pause
exit /b 0
