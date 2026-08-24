@echo off
rem MuMuManager test double for v0.9.5 strict driver routing.
rem Only instance 0 (ADB port 16416) is reported as MuMu; every invocation is logged.
setlocal EnableExtensions
set STUB=%~dp0
set CALLS=%STUB%mumu-calls.log
echo %*>>"%CALLS%"

if /I "%~1"=="info" if "%~2"=="-v" if /I "%~3"=="all" (
  echo {"0":{"adb_port":16416,"is_main":true,"is_process_started":true,"is_android_started":true}}
  exit /b 0
)
if /I "%~1"=="info" if "%~2"=="-v" if "%~3"=="0" (
  if exist "%STUB%stopped.flag" (
    echo {"0":{"adb_port":16416,"is_process_started":false,"is_android_started":false}}
  ) else (
    echo {"0":{"adb_port":16416,"is_process_started":true,"is_android_started":true}}
  )
  exit /b 0
)
if /I "%~1"=="control" if "%~2"=="-v" if "%~3"=="0" if /I "%~4"=="launch" (
  del /q "%STUB%stopped.flag" >nul 2>&1
  echo launched
  exit /b 0
)
if /I "%~1"=="control" if "%~2"=="-v" if "%~3"=="0" if /I "%~4"=="shutdown" (
  >"%STUB%stopped.flag" echo stopped
  echo shutdown complete
  exit /b 0
)
if /I "%~1"=="adb" if "%~2"=="-v" if "%~3"=="0" if /I "%~4"=="connect" (
  echo connected
  exit /b 0
)
if /I "%~1"=="adb" if "%~2"=="-v" if "%~3"=="0" if /I "%~4"=="shell" if /I "%~5"=="dumpsys" if /I "%~6"=="window" (
  type "%STUB%foreground.txt"
  exit /b 0
)
if /I "%~1"=="adb" if "%~2"=="-v" if "%~3"=="0" if /I "%~4"=="shell" if /I "%~5"=="am" if /I "%~6"=="start" (
  echo Starting: Intent { cmp=%~8 }
  if /I "%~8"=="com.bad.game/.MainActivity" (
    echo Error type 3
    echo Error: Activity class {com.bad.game/com.bad.game.MainActivity} does not exist.
  )
  exit /b 0
)
if /I "%~1"=="api" if "%~2"=="-v" if "%~3"=="0" if /I "%~4"=="close_app" (
  echo Stopped %~5
  exit /b 0
)
echo unexpected: %*
exit /b 1
