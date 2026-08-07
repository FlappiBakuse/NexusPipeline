@echo off
setlocal
cd /d "%~dp0"
if exist "%~dp0release" rmdir /s /q "%~dp0release"
dotnet publish "%~dp0src\NexusPipeline.csproj" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -p:DebugSymbols=false -o "%~dp0release"
if errorlevel 1 goto build_failed
xcopy /e /i /y "%~dp0WebRoot" "%~dp0release\WebRoot" >nul
if exist "%~dp0plugins" xcopy /e /i /y "%~dp0plugins" "%~dp0release\plugins" >nul
if not exist "%~dp0release\plugins" mkdir "%~dp0release\plugins"
echo.
echo Build OK: %~dp0release\nexus-pipeline.exe
echo 部署：整体拷贝 release 文件夹到目标目录即可（config/history/logs 运行时自动生成）。
exit /b 0

:build_failed
echo.
echo Build failed. 请检查上方错误信息。
exit /b 1
