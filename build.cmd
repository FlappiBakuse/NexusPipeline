@echo off
setlocal
cd /d "%~dp0"
rem 增量构建（v0.6.4+）：src 无变化时跳过 dotnet publish，仅同步 wwwroot/plugins（前端改动无需重编）；
rem 指纹文件 .build-src-hash 在项目根（不入库），CI 全新检出无此文件 = 全量构建。
for /f "usebackq delims=" %%h in (`powershell -NoProfile -Command "$h=(Get-ChildItem -LiteralPath '%~dp0src' -Recurse -File | Get-FileHash -Algorithm SHA256 | Select-Object -ExpandProperty Hash) -join ''; $b=[System.Text.Encoding]::UTF8.GetBytes($h); $d=[System.Security.Cryptography.SHA256]::Create().ComputeHash($b); [System.BitConverter]::ToString($d).Replace('-','')"`) do set SRC_HASH=%%h
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
if exist "%~dp0plugins" xcopy /e /i /y "%~dp0plugins" "%~dp0release\plugins" >nul
if not exist "%~dp0release\plugins" mkdir "%~dp0release\plugins"
echo.
echo Build OK: %~dp0release\nexus-pipeline.exe
echo 构建类型：提权版（requireAdministrator，程序必须以管理员身份运行）
echo 部署：整体拷贝 release 文件夹到目标目录即可（config/history/logs 运行时自动生成）。
exit /b 0

:build_failed
if exist "%~dp0build-tmp" rmdir /s /q "%~dp0build-tmp"
echo.
echo Build failed. 请检查上方错误信息。
exit /b 1
