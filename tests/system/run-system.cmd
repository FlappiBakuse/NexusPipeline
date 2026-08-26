@echo off
setlocal
cd /d "%~dp0"
net session >nul 2>&1
if errorlevel 1 if not "%NEXUS_SYSTEM_SMOKE_ELEVATED%"=="1" (
    echo System Smoke requires an administrator terminal.
    exit /b 2
)
set NEXUS_SYSTEM_SMOKE=1
set "NEXUS_SYSTEM_RUNTIME_NAME=runtime-runtime"
node --test --test-concurrency=1 runtime-smoke.mjs
if errorlevel 1 exit /b %errorlevel%
set "NEXUS_SYSTEM_RUNTIME_NAME=runtime-judge"
node --test --test-concurrency=1 judge-smoke.mjs
if errorlevel 1 exit /b %errorlevel%
set "NEXUS_SYSTEM_RUNTIME_NAME=runtime-execution-resilience"
node --test --test-concurrency=1 execution-resilience.mjs
if errorlevel 1 exit /b %errorlevel%
set "NEXUS_SYSTEM_RUNTIME_NAME=runtime-emulator"
node --test --test-concurrency=1 emulator-smoke.mjs
if errorlevel 1 exit /b %errorlevel%
set "NEXUS_SYSTEM_RUNTIME_NAME=runtime-update"
node --test --test-concurrency=1 update-smoke.mjs
exit /b %errorlevel%
