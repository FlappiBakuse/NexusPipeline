@echo off
rem Test stub adb (v0.7.0+): responds to host adb call shapes.
rem Foreground app is controlled by foreground.txt; call history appended to calls.log.
set STUB=%~dp0
set CALLS=%STUB%calls.log
if "%~1"=="connect" (
  if exist "%STUB%rebooted.flag" (
    echo failed to connect to %~2
    echo connect %~2 >> "%CALLS%"
    exit /b 1
  )
  if "%~2"=="127.0.0.1:16385" (
    echo cannot connect to %~2: Connection refused
    echo connect %~2 >> "%CALLS%"
    exit /b 0
  )
  echo connected to %~2
  echo connect %~2 >> "%CALLS%"
  exit /b 0
)
if "%~1"=="-s" (
  if "%~3"=="shell" (
    if "%~4"=="echo" (
      echo ok
      exit /b 0
    )
    if "%~4"=="reboot" (
      echo Done
      echo reboot >> "%CALLS%"
      > "%STUB%rebooted.flag" echo offline
      exit /b 0
    )
    if "%~4"=="dumpsys" (
      type "%STUB%foreground.txt"
      exit /b 0
    )
    if "%~4"=="am" (
      if "%~5"=="start" (
        echo Starting: Intent { cmp=%~6 %~7 }
        if "%~7"=="com.bad.game/.MainActivity" (
          echo Error type 3
          echo Error: Activity class {com.bad.game/com.bad.game.MainActivity} does not exist.
        )
        echo start %~6 %~7 >> "%CALLS%"
        exit /b 0
      )
      if "%~5"=="force-stop" (
        echo Stopped %~6
        echo force-stop %~6 >> "%CALLS%"
        exit /b 0
      )
    )
  )
)
echo unexpected: %*
exit /b 1
