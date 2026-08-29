@echo off
setlocal
cd /d "%~dp0QingToolbox.Tauri"
title QingToolbox Tauri development host

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build-tauri-canary.ps1"
if errorlevel 1 (
  echo Failed to build the native module canary. >&2
  pause
  exit /b 1
)

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build-tauri-launcher.ps1"
if errorlevel 1 (
  echo Failed to build the Rust Qing Launcher module. >&2
  pause
  exit /b 1
)

npm run tauri -- dev
set "QING_TAURI_EXIT_CODE=%ERRORLEVEL%"
if not "%QING_TAURI_EXIT_CODE%"=="0" pause
exit /b %QING_TAURI_EXIT_CODE%
