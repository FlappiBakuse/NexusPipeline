@echo off
setlocal
cd /d "%~dp0"
rem 测试服务需要管理员权限；非管理员终端交由 UAC 重新启动本脚本。
net session >nul 2>&1
if errorlevel 1 (
    echo [��ʾ] ��ǰ�ն��޹���ԱȨ�ޣ������Թ���Ա���������������ԣ�UAC �Ӳ�֪ͨʱ�޵�����...
    if "%*"=="" (
        pwsh -NoProfile -Command "Start-Process -FilePath '%~dp0run-e2e.cmd' -Verb RunAs -WorkingDirectory '%~dp0'"
    ) else (
        pwsh -NoProfile -Command "Start-Process -FilePath '%~dp0run-e2e.cmd' -ArgumentList '%*' -Verb RunAs -WorkingDirectory '%~dp0'"
    )
    exit /b 0
)
set PLAYWRIGHT_BROWSERS_PATH=%~dp0browsers
rem UI Smoke 只有一套集合；--realtime 仅保留给需要真实墙钟的专项脚本。
set REALTIME=
for %%a in (%*) do (
    if /i "%%a"=="--realtime" set REALTIME=1
)
if not defined REALTIME set NEXUS_TIME_SCALE=10
npx playwright test
exit /b %errorlevel%
