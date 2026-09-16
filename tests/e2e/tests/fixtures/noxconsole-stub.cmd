@echo off
rem Nox console test double: instance Android7 maps through its vbox ADB port.
setlocal EnableExtensions
set STUB=%~dp0
set CALLS=%STUB%nox-calls.log
echo %*>>"%CALLS%"

if /I "%~1"=="list" (
  if exist "%STUB%name-first.flag" (
    echo nox,NoxPlayer,started
  ) else (
    echo 0,Android7,started
  )
  exit /b 0
)
if /I "%~1"=="launch" if /I "%~2"=="-index:0" (
  echo launched
  exit /b 0
)
if /I "%~1"=="launch" if /I "%~2"=="-name:nox" if exist "%STUB%name-first.flag" (
  echo launched
  exit /b 0
)
if /I "%~1"=="quit" if /I "%~2"=="-index:0" (
  echo quit
  exit /b 0
)
if /I "%~1"=="quit" if /I "%~2"=="-name:nox" if exist "%STUB%name-first.flag" (
  echo quit
  exit /b 0
)
echo unexpected: %*
exit /b 1
