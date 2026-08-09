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
node test.mjs %*
exit /b %errorlevel%
