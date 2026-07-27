@echo off
setlocal
cd /d "%~dp0"

title QingToolbox - Update, Repair, Build and Run

echo [1/5] Checking the toolbox branch...
for /f "delims=" %%B in ('git branch --show-current 2^>nul') do set "CURRENT_BRANCH=%%B"
if /i not "%CURRENT_BRANCH%"=="toolbox" (
    echo.
    echo ERROR: Current branch is "%CURRENT_BRANCH%", expected "toolbox".
    echo Open this script from the QingToolbox toolbox worktree.
    goto :failed
)

echo [2/5] Updating from origin/toolbox...
git pull --ff-only origin toolbox
if errorlevel 1 goto :failed

where node.exe >nul 2>nul
if errorlevel 1 (
    if exist "%ProgramFiles%\nodejs\node.exe" (
        set "PATH=%ProgramFiles%\nodejs;%PATH%"
    ) else if exist "%USERPROFILE%\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe" (
        set "PATH=%USERPROFILE%\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin;%PATH%"
    ) else (
        echo.
        echo ERROR: Node.js was not found. Install Node.js 24 LTS or add node.exe to PATH.
        goto :failed
    )
)

echo [3/5] Checking Web workspace assets...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\verify-web-ui-assets.ps1"
if errorlevel 1 (
    echo.
    echo Web workspace assets are missing or stale. Rebuilding them automatically...
    where npm.cmd >nul 2>nul
    if not errorlevel 1 (
        powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build-web-ui.ps1" -SkipTests
        if errorlevel 1 goto :web_repair_failed
    ) else (
        if not exist "%~dp0QingToolbox.WebUI\node_modules\vite\bin\vite.js" (
            echo ERROR: Web dependencies are missing and npm is unavailable.
            echo Install Node.js 24 LTS, then run npm install in QingToolbox.WebUI.
            goto :web_repair_failed
        )
        pushd "%~dp0QingToolbox.WebUI"
        node.exe node_modules\vue-tsc\bin\vue-tsc.js --noEmit
        if errorlevel 1 (popd & goto :web_repair_failed)
        node.exe node_modules\vite\bin\vite.js build
        if errorlevel 1 (popd & goto :web_repair_failed)
        node.exe tools\assets.mjs generate
        if errorlevel 1 (popd & goto :web_repair_failed)
        node.exe tools\assets.mjs verify
        if errorlevel 1 (popd & goto :web_repair_failed)
        popd
    )
)

echo [4/5] Building and deploying development modules...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\deploy-dev-modules.ps1"
if errorlevel 1 goto :failed

echo [5/5] Starting QingToolbox...
set "SHELL_EXE=%~dp0QingToolbox.Shell\bin\Debug\net10.0-windows\QingToolbox.Shell.exe"
set "REPOSITORY_ROOT=%~dp0"
if "%REPOSITORY_ROOT:~-1%"=="\" set "REPOSITORY_ROOT=%REPOSITORY_ROOT:~0,-1%"
if not exist "%SHELL_EXE%" (
    echo ERROR: Shell executable was not produced: "%SHELL_EXE%"
    goto :failed
)

start "" "%SHELL_EXE%" --environment Development --profile Shell --repo-root "%REPOSITORY_ROOT%"
exit /b 0

:web_repair_failed
echo.
echo ERROR: Automatic Web workspace repair failed.
goto :failed

:failed
echo.
echo QingToolbox could not be started. Review the error above.
pause
exit /b 1
