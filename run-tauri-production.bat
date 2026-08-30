@echo off
setlocal
cd /d "%~dp0"
title QingToolbox - Tauri production build

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build-tauri-production.ps1" -Smoke
if errorlevel 1 (
  echo Failed to prepare the Tauri production package. >&2
  pause
  exit /b 1
)

start "QingToolbox" "%~dp0artifacts\tauri-production\QingToolbox\QingToolbox.exe"
exit /b 0
