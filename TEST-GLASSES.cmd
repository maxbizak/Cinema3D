@echo off
rem Glasses test pattern: each eye must see ONLY its own word (LEFT / RIGHT).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0app\launch.ps1" -Mode dlp -Test %*
if errorlevel 1 pause
