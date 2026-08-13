@echo off
setlocal
cd /d "%~dp0"
rem 提权版（requireAdministrator）无法在无管理员权限下由 node 启动（CreateProcess 报 740），
rem 非管理员终端自动以管理员身份重启本脚本（UAC 从不通知 + 管理员账户时静默提权，无弹窗）。
net session >nul 2>&1
if errorlevel 1 (
    echo [提示] 当前终端无管理员权限，正在以管理员身份重新启动测试（UAC 从不通知时无弹窗）...
    powershell -NoProfile -Command "Start-Process -FilePath '%~dp0run-uitest.cmd' -ArgumentList '%*' -Verb RunAs -WorkingDirectory '%~dp0'"
    exit /b 0
)
set PLAYWRIGHT_BROWSERS_PATH=%~dp0browsers
rem 参数解析（v0.6.2+）：--ci = 核心回归集（NEXUS_CI=1，剔除响应式外壳外观用例）；
rem --realtime = 关闭时间加速（真实计时档，发布前全量回归用）；其余情况默认 NEXUS_TIME_SCALE=60 加速档。
set REALTIME=
for %%a in (%*) do (
    if /i "%%a"=="--ci" set NEXUS_CI=1
    if /i "%%a"=="--realtime" set REALTIME=1
)
if not defined REALTIME set NEXUS_TIME_SCALE=60
npx playwright test
exit /b %errorlevel%
