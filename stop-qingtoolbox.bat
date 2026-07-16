@echo off
setlocal
title Stop QingToolbox

echo Checking for QingToolbox.Shell.exe...
tasklist /FI "IMAGENAME eq QingToolbox.Shell.exe" 2>NUL | find /I "QingToolbox.Shell.exe" >NUL

if errorlevel 1 (
    echo QingToolbox is not running.
) else (
    echo Stopping QingToolbox and its child processes...
    taskkill /F /T /IM QingToolbox.Shell.exe
    if errorlevel 1 (
        echo Failed to stop QingToolbox. Try running this file as administrator.
    ) else (
        echo QingToolbox has been stopped successfully.
    )
)

echo.
pause
endlocal
