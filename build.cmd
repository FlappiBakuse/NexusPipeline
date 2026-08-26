@echo off
setlocal
cd /d "%~dp0"
rem ����������v0.6.4+����src �ޱ仯ʱ���� dotnet publish����ͬ�� wwwroot/plugins��ǰ�˸Ķ������رࣩ��
rem ָ���ļ� .build-src-hash ����Ŀ��������⣩��CI ȫ�¼���޴��ļ� = ȫ��������
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
if exist "%~dp0plugins" xcopy /e /i /y "%~dp0plugins" "%~dp0release\plugins" >nul
if not exist "%~dp0release\plugins" mkdir "%~dp0release\plugins"
echo.
echo Build OK: %~dp0release\nexus-pipeline.exe
echo �������ͣ���Ȩ�棨requireAdministrator����������Թ���Ա�������У�
echo �������忽�� release �ļ��е�Ŀ��Ŀ¼���ɣ�config/history/logs ����ʱ�Զ����ɣ���
exit /b 0

:build_failed
if exist "%~dp0build-tmp" rmdir /s /q "%~dp0build-tmp"
echo.
echo Build failed. �����Ϸ�������Ϣ��
exit /b 1
