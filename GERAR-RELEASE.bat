@echo off
setlocal
cd /d "%~dp0"

call PUBLICAR-WINDOWS.bat
if errorlevel 1 exit /b 1

echo.
set /p DOWNLOAD_URL=URL publica do ZIP (Enter para preencher depois): 
if "%DOWNLOAD_URL%"=="" set DOWNLOAD_URL=COLE_A_URL_PUBLICA_DO_ZIP_AQUI

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\gerar-release.ps1" -DownloadUrl "%DOWNLOAD_URL%"
if errorlevel 1 (
  pause
  exit /b 1
)

echo.
echo Agora publique o ZIP e o latest.json em um local acessivel pelos computadores da equipe.
pause
