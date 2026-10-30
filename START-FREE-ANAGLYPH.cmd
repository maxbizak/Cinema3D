@echo off
rem Fallback: red-cyan anaglyph (works at 60 Hz, needs paper red-cyan glasses).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0app\launch.ps1" -Mode anaglyph %*
if errorlevel 1 pause
