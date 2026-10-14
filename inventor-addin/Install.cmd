@echo off
rem SF Vault Bridge installer: double-click. Needs Inventor 2026 and nothing else.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Package.ps1"
if errorlevel 1 (echo. & echo The install did not finish. Read the message above.)
echo.
pause
