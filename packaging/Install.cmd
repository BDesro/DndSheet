@echo off
rem Double-click to install Hearthsheet for the current user.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" %*
echo.
pause
