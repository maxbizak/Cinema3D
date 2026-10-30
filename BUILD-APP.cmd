@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\build-app.ps1"
if errorlevel 1 (
  echo Build failed. Previous Cinema3D.exe is preserved.
  pause
  exit /b 1
)
echo Build complete. Restart Cinema3D.exe.
pause
