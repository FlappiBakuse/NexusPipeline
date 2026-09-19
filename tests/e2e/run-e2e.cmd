@echo off
setlocal
cd /d "%~dp0"
node "%~dp0..\run.mjs" dev ui %*
exit /b %errorlevel%
