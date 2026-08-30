@echo off
setlocal
cd /d "%~dp0"
title QingToolbox - Tauri Update, Build and Run

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\run-tauri-latest.ps1" -Configuration Release
set "QINGTOOLBOX_EXIT_CODE=%ERRORLEVEL%"
if "%QINGTOOLBOX_EXIT_CODE%"=="0" exit /b 0

echo.
echo QingToolbox Tauri could not be started. Review the diagnostics above.
pause
exit /b %QINGTOOLBOX_EXIT_CODE%
