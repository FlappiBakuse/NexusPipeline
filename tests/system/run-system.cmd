@echo off
setlocal
cd /d "%~dp0"
node "%~dp0..\run.mjs" codex system %*
exit /b %errorlevel%
