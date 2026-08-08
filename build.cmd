@echo off
setlocal
cd /d "%~dp0"
set "ELEVATE=1"
if /i "%~1"=="/test" set "ELEVATE=0"
if exist "%~dp0release" rmdir /s /q "%~dp0release"
dotnet publish "%~dp0src\NexusPipeline.csproj" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -p:DebugSymbols=false -p:Elevate=%ELEVATE% -o "%~dp0release"
if errorlevel 1 goto build_failed
if exist "%~dp0release\plugins" rmdir /s /q "%~dp0release\plugins"
xcopy /e /i /y "%~dp0wwwroot" "%~dp0release\wwwroot" >nul
if exist "%~dp0plugins" xcopy /e /i /y "%~dp0plugins" "%~dp0release\plugins" >nul
if not exist "%~dp0release\plugins" mkdir "%~dp0release\plugins"
rem 外部专用插件（与主程序解耦，随发布分发，可删可换）
dotnet publish "%~dp0extensions\BetterGIAdapter\BetterGIAdapter.csproj" -c Release -p:PublishSingleFile=false -o "%~dp0build-tmp\plugins" >nul
if errorlevel 1 goto build_failed
copy /y "%~dp0build-tmp\plugins\BetterGIAdapter.dll" "%~dp0release\plugins\" >nul
dotnet publish "%~dp0extensions\March7thAssistantAdapter\March7thAssistantAdapter.csproj" -c Release -p:PublishSingleFile=false -o "%~dp0build-tmp\plugins" >nul
if errorlevel 1 goto build_failed
copy /y "%~dp0build-tmp\plugins\March7thAssistantAdapter.dll" "%~dp0release\plugins\" >nul
rmdir /s /q "%~dp0build-tmp"
echo.
echo Build OK: %~dp0release\nexus-pipeline.exe
if "%ELEVATE%"=="1" (echo 构建类型：提权版（requireAdministrator，正式发布）) else (echo 构建类型：无提权版（/test，CI 与 e2e 用）)
echo 部署：整体拷贝 release 文件夹到目标目录即可（config/history/logs 运行时自动生成）。
exit /b 0

:build_failed
if exist "%~dp0build-tmp" rmdir /s /q "%~dp0build-tmp"
echo.
echo Build failed. 请检查上方错误信息。
exit /b 1
