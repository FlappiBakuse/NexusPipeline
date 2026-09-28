@echo off
setlocal
cd /d "%~dp0"
rem This produces the production release package. Functional qualification uses a separate Test Host.
rem Plugin repository is maintained separately; frontend source is built into static files before publishing.
rem Frontend readiness fingerprint (.generated\frontend-build.hash) is written by node tests\run.mjs.
if not "%~1"=="" goto usage_failed
call npm ci --prefix "%~dp0frontend" --no-audit --no-fund
if errorlevel 1 goto frontend_failed
call npm run typecheck --prefix "%~dp0frontend"
if errorlevel 1 goto frontend_failed
call npm run build --prefix "%~dp0frontend"
if errorlevel 1 goto frontend_failed
rem Publish into the existing installation directory without deleting runtime-owned data.
if not exist "%~dp0release" mkdir "%~dp0release"
dotnet publish "%~dp0src\NexusPipeline.csproj" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -p:DebugSymbols=false -o "%~dp0release"
if errorlevel 1 goto build_failed
rem wwwroot is an owned frontend build output; plugins/config/data/history/logs are runtime-owned.
if exist "%~dp0release\wwwroot" rmdir /s /q "%~dp0release\wwwroot"
if errorlevel 1 goto build_failed
xcopy /e /i /y "%~dp0frontend\dist" "%~dp0release\wwwroot" >nul
if errorlevel 1 goto build_failed
if not exist "%~dp0release\plugins" mkdir "%~dp0release\plugins" >nul 2>nul
echo.
echo Build OK: %~dp0release\nexus-pipeline.exe
echo Production executable uses the requireAdministrator manifest.
echo Functional qualification uses an isolated asInvoker Test Host; this executable remains the production requireAdministrator build.
echo Runtime data directories are created on first launch.
exit /b 0

:build_failed
if exist "%~dp0build-tmp" rmdir /s /q "%~dp0build-tmp"
echo.
echo Build failed. See the command output above.
exit /b 1

:frontend_failed
echo.
echo Frontend build failed. See the command output above.
exit /b 1

:usage_failed
echo Usage: build.cmd
exit /b 2
