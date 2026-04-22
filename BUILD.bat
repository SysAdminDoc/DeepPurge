@echo off
title DeepPurge Builder v0.8.1
echo.
echo   ============================================
echo     DeepPurge Builder
echo   ============================================
echo.
echo   Building portable executable...
echo   .NET 8 SDK will be auto-installed if needed.
echo.

:: Run the build script with proper execution policy
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build.ps1" -OpenOutput

echo.
if %ERRORLEVEL% EQU 0 (
    echo   Done! Check the build\ folder for DeepPurge.exe
) else (
    echo   Build failed. See errors above.
)
echo.
pause
