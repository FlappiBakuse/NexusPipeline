@echo off
setlocal
cd /d "%~dp0"
node "%~dp0..\run.mjs" codex ui %*
exit /b %errorlevel%
