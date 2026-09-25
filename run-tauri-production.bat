@echo off
setlocal
set "QING_TAURI_DISABLE_AUTOSTART_SYNC=1"
cd /d "%~dp0"
title QingToolbox - Tauri local Release host

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\run-tauri-latest.ps1" -Configuration Release -SkipUpdate
if errorlevel 1 (
  echo Failed to prepare the Tauri production package. >&2
  pause
  exit /b 1
)

exit /b 0
