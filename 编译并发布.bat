@echo off
setlocal
title yuSnip 3-channel release (GitHub + Website + Changelog)
set "yuSnipRoot=%~dp0"
cd /d "%yuSnipRoot%" || exit /b 1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%yuSnipRoot%tools\PublishAll.ps1" %*
if errorlevel 1 (
    echo Release failed. Review the messages above.
    if /i "%~1"=="-CheckOnly" exit /b 1
    pause
    exit /b 1
)
if /i "%~1"=="-CheckOnly" exit /b 0
echo Release workflow finished.
timeout /t 3 >nul
exit /b 0
