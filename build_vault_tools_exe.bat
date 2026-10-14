@echo off
rem Builds inventor-addin\tools\SFVaultTools.exe from vault_tools_cli.py (windowed, one file).
cd /d "%~dp0"
py -3.14 -m pip install --quiet pandas openpyxl xlrd pypdf reportlab msal httpx pyinstaller || exit /b 1
py -3.14 -m PyInstaller --onefile --windowed --noconfirm --clean --name SFVaultTools ^
  --distpath "%~dp0inventor-addin\tools" --workpath "%~dp0build\sfvaulttools" --specpath "%~dp0build\sfvaulttools" ^
  --add-data "%~dp0Simplifyber_Logo.png;." --add-data "%~dp0Simplifyber_Logo_White.png;." ^
  --collect-all msal --hidden-import httpx "%~dp0vault_tools_cli.py" || exit /b 1
if not exist "%~dp0inventor-addin\tools\SFVaultTools.exe" exit /b 1
echo Built inventor-addin\tools\SFVaultTools.exe
