@echo off
setlocal DisableDelayedExpansion

rem This entry point is intentionally small: all validation and orchestration lives in
rem scripts/publish-preview-release.ps1.  Keep delayed expansion disabled so an input
rem containing ! is never re-expanded by cmd.exe.
set "SCRIPT_DIR=%~dp0"

echo Latest QingMo-A/QingToolbox release:
rem `gh release view` without a tag ignores repositories that only have
rem prereleases.  The product is currently alpha-only, so list the newest
rem published Release explicitly instead.
gh release list --repo QingMo-A/QingToolbox --limit 1 --json tagName,name,publishedAt --template "{{range .}}{{.tagName}} ({{.name}}){{end}}"
if errorlevel 1 (
    echo Unable to query the latest QingMo-A/QingToolbox Release. >&2
    pause
    exit /b 1
)
echo.

set "QING_RELEASE_VERSION_FIRST="
set /p "QING_RELEASE_VERSION_FIRST=Target SemVer (enter again to confirm): "
set "QING_RELEASE_VERSION_SECOND="
set /p "QING_RELEASE_VERSION_SECOND=Target SemVer (confirm): "

if not defined QING_RELEASE_VERSION_FIRST (
    echo A target version is required. >&2
    pause
    exit /b 2
)
if not defined QING_RELEASE_VERSION_SECOND (
    echo The target version confirmation is required. >&2
    pause
    exit /b 2
)

rem Do not interpolate either value into a command line.  The child process reads
rem these inherited environment values as data, then performs the exact-match and
rem SemVer checks before it can invoke git or gh.
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%scripts\publish-preview-release.ps1" -ReadVersionFromEnvironment
set "QING_RELEASE_EXIT_CODE=%errorlevel%"
if not "%QING_RELEASE_EXIT_CODE%"=="0" echo Preview Release hand-off failed with exit code %QING_RELEASE_EXIT_CODE%. >&2
pause
exit /b %QING_RELEASE_EXIT_CODE%
