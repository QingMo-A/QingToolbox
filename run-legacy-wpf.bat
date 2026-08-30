@echo off
setlocal
cd /d "%~dp0"
title QingToolbox - Legacy WPF development host

echo This entry is retained only for legacy WPF maintenance.
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\run-latest.ps1" %*
set "QING_WPF_EXIT_CODE=%ERRORLEVEL%"
if not "%QING_WPF_EXIT_CODE%"=="0" pause
exit /b %QING_WPF_EXIT_CODE%
