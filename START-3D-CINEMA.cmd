@echo off
rem 3D Cinema: free DLP-Link 3D player (frame-sequential 120 Hz) on the projector.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0app\launch.ps1" -Mode dlp %*
if errorlevel 1 pause
