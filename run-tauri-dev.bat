@echo off
setlocal
cd /d "%~dp0"
title QingToolbox Tauri development host

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\run-tauri-latest.ps1" -Configuration Debug -SkipUpdate
set "QING_TAURI_EXIT_CODE=%ERRORLEVEL%"
if not "%QING_TAURI_EXIT_CODE%"=="0" pause
exit /b %QING_TAURI_EXIT_CODE%
