@echo off
setlocal
cd /d "%~dp0"
rem v0.10.8 起插件由独立仓库管理；源码指纹只覆盖宿主 src，前端静态资源单独同步。
rem .build-src-hash records the host source fingerprint used to skip unchanged publishes.
for /f "usebackq delims=" %%h in (`node "%~dp0tools\source-hash.mjs"`) do set SRC_HASH=%%h
if not exist "%~dp0release\nexus-pipeline.exe" goto do_publish
if not exist "%~dp0.build-src-hash" goto do_publish
set /p OLD_HASH=<"%~dp0.build-src-hash"
if "%OLD_HASH%"=="%SRC_HASH%" goto sync_web
:do_publish
if exist "%~dp0release" rmdir /s /q "%~dp0release"
dotnet publish "%~dp0src\NexusPipeline.csproj" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -p:DebugSymbols=false -o "%~dp0release"
if errorlevel 1 goto build_failed
> "%~dp0.build-src-hash" echo %SRC_HASH%
:sync_web
if exist "%~dp0release\plugins" rmdir /s /q "%~dp0release\plugins"
xcopy /e /i /y "%~dp0wwwroot" "%~dp0release\wwwroot" >nul
mkdir "%~dp0release\plugins" >nul 2>nul
> "%~dp0release\plugins\.nxp-root" echo {"owner":"NexusPipeline","purpose":"plugin-runtime-root","version":1}
echo.
echo Build OK: %~dp0release\nexus-pipeline.exe
echo Run from an Administrator command prompt for HttpListener and system smoke tests.
echo Runtime data directories are created on first launch.
exit /b 0

:build_failed
if exist "%~dp0build-tmp" rmdir /s /q "%~dp0build-tmp"
echo.
echo Build failed. See the command output above.
exit /b 1
