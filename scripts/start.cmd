@echo off
REM Builds the screen and starts CI/CD Pipeline Generator at http://localhost:5090
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0start.ps1"
