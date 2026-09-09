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
echo Verificando processos locais que podem bloquear o build...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Unblock-File -LiteralPath '%~dp0scripts\encerrar-processos-dev.ps1' -ErrorAction SilentlyContinue; & '%~dp0scripts\encerrar-processos-dev.ps1'"

if exist "%~dp0src\SoftcomSmartProvisioner\bin\Debug\net8.0-windows\tools\adb.exe" (
  "%~dp0src\SoftcomSmartProvisioner\bin\Debug\net8.0-windows\tools\adb.exe" kill-server >nul 2>nul
)

dotnet restore
if errorlevel 1 (
  pause
  exit /b 1
)

dotnet build src\SoftcomSmartProvisioner\SoftcomSmartProvisioner.csproj -c Debug
if errorlevel 1 (
  pause
  exit /b 1
)

echo.
echo Preparando Updater para testes locais...
if exist "%TEMP%\SoftcomProvisionerUpdaterBuild" rmdir /s /q "%TEMP%\SoftcomProvisionerUpdaterBuild"
dotnet publish src\SoftcomSmartProvisioner.Updater\SoftcomSmartProvisioner.Updater.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "%TEMP%\SoftcomProvisionerUpdaterBuild"
if errorlevel 1 (
  pause
  exit /b 1
)
copy /y "%TEMP%\SoftcomProvisionerUpdaterBuild\SoftcomSmartProvisioner.Updater.exe" "src\SoftcomSmartProvisioner\bin\Debug\net8.0-windows\SoftcomSmartProvisioner.Updater.exe" >nul
rmdir /s /q "%TEMP%\SoftcomProvisionerUpdaterBuild"

dotnet run --no-build --project src\SoftcomSmartProvisioner\SoftcomSmartProvisioner.csproj
if errorlevel 1 pause
