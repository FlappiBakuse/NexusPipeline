@echo off
rem LDPlayer console test double: index 0 maps to the candidate ADB port 5554.
setlocal EnableExtensions
set STUB=%~dp0
set CALLS=%STUB%ld-calls.log
echo %*>>"%CALLS%"

if /I "%~1"=="list2" (
  echo 0,LDPlayer,started
  exit /b 0
)
if /I "%~1"=="launch" if /I "%~2"=="--index" if "%~3"=="0" (
  echo launched
  exit /b 0
)
if /I "%~1"=="quit" if /I "%~2"=="--index" if "%~3"=="0" (
  echo quit
  exit /b 0
)
echo unexpected: %*
exit /b 1
