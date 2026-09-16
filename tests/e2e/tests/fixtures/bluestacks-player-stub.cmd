@echo off
rem BlueStacks HD-Player test double: EnsureReady must pass the instance identity.
setlocal EnableExtensions
set STUB=%~dp0
set CALLS=%STUB%bluestacks-calls.log
echo %*>>"%CALLS%"

if /I "%~1"=="--instance" if /I "%~2"=="Pie64" (
  echo launched
  exit /b 0
)
echo unexpected: %*
exit /b 1
