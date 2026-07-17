@echo off
setlocal
cd /d "%~dp0"

title QingToolbox - Update, Build and Run

echo [1/4] Checking the toolbox branch...
for /f "delims=" %%B in ('git branch --show-current 2^>nul') do set "CURRENT_BRANCH=%%B"
if /i not "%CURRENT_BRANCH%"=="toolbox" (
    echo.
    echo ERROR: Current branch is "%CURRENT_BRANCH%", expected "toolbox".
    echo Open this script from the QingToolbox toolbox worktree.
    goto :failed
)

echo [2/4] Updating from origin/toolbox...
git pull --ff-only origin toolbox
if errorlevel 1 goto :failed

echo [3/4] Building and deploying development modules...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\deploy-dev-modules.ps1"
if errorlevel 1 goto :failed

echo [4/4] Starting QingToolbox...
set "SHELL_EXE=%~dp0QingToolbox.Shell\bin\Debug\net10.0-windows\QingToolbox.Shell.exe"
set "REPOSITORY_ROOT=%~dp0"
if "%REPOSITORY_ROOT:~-1%"=="\" set "REPOSITORY_ROOT=%REPOSITORY_ROOT:~0,-1%"
if not exist "%SHELL_EXE%" (
    echo ERROR: Shell executable was not produced: "%SHELL_EXE%"
    goto :failed
)

start "" "%SHELL_EXE%" --environment Development --profile Shell --repo-root "%REPOSITORY_ROOT%"
exit /b 0

:failed
echo.
echo QingToolbox could not be started. Review the error above.
pause
exit /b 1
