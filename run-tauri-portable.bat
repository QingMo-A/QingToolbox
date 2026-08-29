@echo off
setlocal
cd /d "%~dp0"
title QingToolbox - Tauri portable preview

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build-tauri-portable.ps1" -SkipModuleBuild -Smoke
if errorlevel 1 (
  echo Failed to prepare the Tauri portable preview. >&2
  pause
  exit /b 1
)

start "QingToolbox" "%~dp0artifacts\tauri-portable\QingToolbox\QingToolbox.exe"
exit /b 0
