@echo off
REM One-click build: tests + standalone Windows .exe  ->  dist\MediaOrganizer\MediaOrganizer.exe
REM Options: build.bat -SkipTests   (skip the test run)   |   build.bat -SkipTools (do not download ffprobe)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
if errorlevel 1 (
  echo.
  echo BUILD FAILED. See the messages above.
  pause
  exit /b 1
)
echo.
pause
