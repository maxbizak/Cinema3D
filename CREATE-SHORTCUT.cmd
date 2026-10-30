@echo off
rem Creates desktop icons "3D Cinema" and "Add 3D movie". Run once after unpacking.
chcp 65001 >nul
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\create-shortcut.ps1"
pause
