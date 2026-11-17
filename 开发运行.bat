@echo off
setlocal
rem Dev fast loop: incremental JIT build (seconds) + run. For daily development only.
rem Use the release entry for GitHub Releases.
title yuSnip dev build + run
set "yuSnipRoot=%~dp0"
cd /d "%yuSnipRoot%" || exit /b 1
powershell -NoProfile -ExecutionPolicy Bypass -File "%yuSnipRoot%tools\DevRun.ps1"
if errorlevel 1 (
    echo.
    echo ========================================
    echo  Build/Run FAILED! See messages above.
    echo  Press any key to close this window...
    echo ========================================
    pause >nul
    exit /b 1
)
