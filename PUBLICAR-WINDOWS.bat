@echo off
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo.
  echo .NET SDK nao localizado.
  echo Instale o .NET 8 SDK e execute novamente.
  pause
  exit /b 1
)

echo.
echo Verificando processos locais que podem bloquear a publicacao...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Unblock-File -LiteralPath '%~dp0scripts\encerrar-processos-dev.ps1' -ErrorAction SilentlyContinue; & '%~dp0scripts\encerrar-processos-dev.ps1'"

if exist "%~dp0src\SoftcomSmartProvisioner\bin\Debug\net8.0-windows\tools\adb.exe" (
  "%~dp0src\SoftcomSmartProvisioner\bin\Debug\net8.0-windows\tools\adb.exe" kill-server >nul 2>nul
)
if exist "%~dp0src\SoftcomSmartProvisioner\bin\Release\net8.0-windows\tools\adb.exe" (
  "%~dp0src\SoftcomSmartProvisioner\bin\Release\net8.0-windows\tools\adb.exe" kill-server >nul 2>nul
)

if exist publish rmdir /s /q publish

dotnet restore
if errorlevel 1 (
  pause
  exit /b 1
)

echo.
echo Publicando Provisioner...
dotnet publish src\SoftcomSmartProvisioner\SoftcomSmartProvisioner.csproj -c Release -r win-x64 --self-contained true -o publish\win-x64
if errorlevel 1 (
  pause
  exit /b 1
)

echo.
echo Publicando Updater...
dotnet publish src\SoftcomSmartProvisioner.Updater\SoftcomSmartProvisioner.Updater.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish\updater-temp
if errorlevel 1 (
  pause
  exit /b 1
)

copy /y "publish\updater-temp\SoftcomSmartProvisioner.Updater.exe" "publish\win-x64\SoftcomSmartProvisioner.Updater.exe" >nul
rmdir /s /q "publish\updater-temp"

echo.
echo Publicacao concluida em:
echo %~dp0publish\win-x64
echo.
echo IMPORTANTE: distribua a pasta win-x64 inteira, nao apenas o EXE.
pause
