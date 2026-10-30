@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\install-laptop.ps1"
if errorlevel 1 (
  echo Install failed. See the message above.
  pause
  exit /b 1
)
echo Installed. Use the 3D Cinema desktop shortcut.
pause
