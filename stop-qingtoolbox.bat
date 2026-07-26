@echo off
setlocal
cd /d "%~dp0"
title Stop QingToolbox Development Host

echo Looking for the QingToolbox development host from this workspace...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\stop-dev-host.ps1" -RepositoryRoot "%~dp0"
set "EXIT_CODE=%ERRORLEVEL%"

echo.
if not "%EXIT_CODE%"=="0" (
    echo The development host could not be stopped. The installed QingToolbox was not targeted.
)
pause
endlocal & exit /b %EXIT_CODE%
