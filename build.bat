@echo off
title Building Resource Analyzer for Windows
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
if %ERRORLEVEL% equ 0 (
    echo.
    echo ==================================================
    echo Build completed successfully!
    echo ==================================================
) else (
    echo.
    echo [ERROR] Build failed with exit code %ERRORLEVEL%.
)
pause
