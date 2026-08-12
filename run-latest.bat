@echo off
setlocal
cd /d "%~dp0"
title QingToolbox - Update, Repair, Build and Run

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\run-latest.ps1"
set "QINGTOOLBOX_EXIT_CODE=%ERRORLEVEL%"
if "%QINGTOOLBOX_EXIT_CODE%"=="0" exit /b 0

echo.
echo QingToolbox could not be started. The launcher printed the diagnostics log path above.
pause
exit /b %QINGTOOLBOX_EXIT_CODE%
