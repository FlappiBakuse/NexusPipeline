@echo off
setlocal
if not "%~1"=="" goto usage_failed
node "%~dp0tests\run.mjs" release
exit /b %errorlevel%

:usage_failed
echo Usage: build.cmd
exit /b 2
