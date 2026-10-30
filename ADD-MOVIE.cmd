@echo off
rem Add a 3D movie (SBS / OU) to the library: double-click to pick a file,
rem or drag one or more movie files onto this file.
chcp 65001 >nul
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\import-movie.ps1" %*
pause
