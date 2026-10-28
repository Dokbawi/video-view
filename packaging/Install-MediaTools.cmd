@echo off
setlocal
echo VideoView media tools setup
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-MediaTools.ps1"
if errorlevel 1 (
    echo Setup failed. Read the message above. You can also configure FFmpeg paths in VideoView.
    pause
    exit /b 1
)
pause
