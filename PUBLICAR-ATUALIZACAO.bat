@echo off
setlocal
cd /d "%~dp0"

echo.
echo Softcom Smart Provisioner - Publicacao automatica no GitHub
powershell -NoProfile -ExecutionPolicy Bypass -Command "Unblock-File -LiteralPath '%~dp0scripts\publicar-atualizacao.ps1' -ErrorAction SilentlyContinue; & '%~dp0scripts\publicar-atualizacao.ps1'"
if errorlevel 1 (
  echo.
  echo Falha ao preparar a atualizacao.
  pause
  exit /b 1
)

echo.
echo Publicacao enviada ao GitHub Actions.
pause
