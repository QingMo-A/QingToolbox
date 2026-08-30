@echo off
setlocal
cd /d "%~dp0"
title Stop QingToolbox Tauri Host

echo Looking for the QingToolbox Tauri host from this workspace...
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\stop-tauri-host.ps1" -RepositoryRoot "%~dp0"
set "EXIT_CODE=%ERRORLEVEL%"

echo.
if not "%EXIT_CODE%"=="0" (
    echo The Tauri host could not be stopped. The installed QingToolbox was not targeted.
)
pause
endlocal & exit /b %EXIT_CODE%
