@echo off
setlocal
cd /d "%~dp0"
net session >nul 2>&1
if errorlevel 1 if not "%NEXUS_SYSTEM_SMOKE_ELEVATED%"=="1" (
    echo [提示] System Smoke 需要管理员终端，请以管理员身份重新运行。
    exit /b 2
)
set NEXUS_SYSTEM_SMOKE=1
node --test --test-concurrency=1 runtime-smoke.mjs
if errorlevel 1 exit /b %errorlevel%
node --test --test-concurrency=1 judge-smoke.mjs
if errorlevel 1 exit /b %errorlevel%
node --test --test-concurrency=1 emulator-smoke.mjs
exit /b %errorlevel%
