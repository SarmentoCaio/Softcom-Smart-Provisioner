@echo off
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"

set "ADB=%CD%\tools\adb.exe"
if not exist "%ADB%" (
  echo [ERRO] tools\adb.exe nao foi localizado.
  pause
  exit /b 1
)

"%ADB%" start-server >nul 2>&1

echo.
echo ================================================
echo   MAPEAMENTO SMART 8.0 - SOFTCOM PROVISIONER
echo ================================================
echo.
"%ADB%" devices -l
echo.

set "SERIAL=%~1"
if "%SERIAL%"=="" set /p "SERIAL=Informe o serial ADB do aparelho (ex.: KM54257740097): "
if "%SERIAL%"=="" (
  echo [ERRO] Serial nao informado.
  pause
  exit /b 1
)

"%ADB%" -s "%SERIAL%" get-state >nul 2>&1
if errorlevel 1 (
  echo [ERRO] O dispositivo %SERIAL% nao esta acessivel pelo ADB.
  pause
  exit /b 1
)

for /f "tokens=1-3 delims=/ " %%a in ('echo %date%') do set "D=%%c%%b%%a"
set "T=%time: =0%"
set "T=%T::=%"
set "T=%T:.=%"
set "OUT=%CD%\MAPEAMENTO-SMART80-%SERIAL%-%D%-%T%"
mkdir "%OUT%" >nul 2>&1

call :capture geral

echo.
echo ETAPA 1
 echo Abra manualmente o Softcom Smart 2 e deixe exatamente na tela de LOGIN,
 echo com a engrenagem visivel. NAO clique na engrenagem ainda.
pause
call :capture 01-login

echo.
echo ETAPA 2
 echo Agora clique MANUALMENTE na engrenagem e aguarde a tela de configuracao abrir.
pause
call :capture 02-configuracao

echo.
echo Mapeamento concluido em:
echo %OUT%
echo.
echo Compacte essa pasta em ZIP e envie no chat.
pause
exit /b 0

:capture
set "NAME=%~1"
echo [INFO] Capturando %NAME%...
"%ADB%" devices -l > "%OUT%\%NAME%-adb-devices.txt" 2>&1
"%ADB%" -s "%SERIAL%" shell getprop ro.build.version.release > "%OUT%\%NAME%-android-version.txt" 2>&1
"%ADB%" -s "%SERIAL%" shell dumpsys package softcom.mobile.smart2 > "%OUT%\%NAME%-package.txt" 2>&1
"%ADB%" -s "%SERIAL%" shell dumpsys window windows > "%OUT%\%NAME%-window.txt" 2>&1
"%ADB%" -s "%SERIAL%" shell dumpsys activity activities > "%OUT%\%NAME%-activities.txt" 2>&1
"%ADB%" -s "%SERIAL%" shell cmd package resolve-activity --brief -a android.intent.action.MAIN -c android.intent.category.LAUNCHER softcom.mobile.smart2 > "%OUT%\%NAME%-resolve-activity.txt" 2>&1
if /I "%NAME%"=="01-login" (
  echo IGNORADO: o Smart 8.0 no Android 7 nao fica idle na LoginActivity; executar uiautomator dump nesta tela pode retirar o APK do primeiro plano. > "%OUT%\%NAME%-uiautomator-command.txt"
) else (
  "%ADB%" -s "%SERIAL%" shell uiautomator dump /sdcard/softcom-smart80-map.xml > "%OUT%\%NAME%-uiautomator-command.txt" 2>&1
  "%ADB%" -s "%SERIAL%" pull /sdcard/softcom-smart80-map.xml "%OUT%\%NAME%-ui.xml" >nul 2>&1
)
"%ADB%" -s "%SERIAL%" shell screencap -p /sdcard/softcom-smart80-map.png >nul 2>&1
"%ADB%" -s "%SERIAL%" pull /sdcard/softcom-smart80-map.png "%OUT%\%NAME%-screen.png" >nul 2>&1
"%ADB%" -s "%SERIAL%" shell rm -f /sdcard/softcom-smart80-map.xml /sdcard/softcom-smart80-map.png >nul 2>&1
"%ADB%" -s "%SERIAL%" logcat -d -v time > "%OUT%\%NAME%-logcat.txt" 2>&1
exit /b 0
