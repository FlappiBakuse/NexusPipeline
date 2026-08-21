@echo off
setlocal
cd /d "%~dp0"
rem ��Ȩ�棨requireAdministrator���޷����޹���ԱȨ������ node ������CreateProcess �� 740����
rem �ǹ���Ա�ն��Զ��Թ���Ա�����������ű���UAC �Ӳ�֪ͨ + ����Ա�˻�ʱ��Ĭ��Ȩ���޵�������
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
rem ����������v0.6.4+����--ci = ���Ļع鼯��NEXUS_CI=1���޳���Ӧʽ��������������
rem --realtime = �ر�ʱ����٣���ʵ��ʱ��������ǰ���ջع��ã����������Ĭ�� NEXUS_TIME_SCALE=10 ���ٵ���
set REALTIME=
for %%a in (%*) do (
    if /i "%%a"=="--ci" set NEXUS_CI=1
    if /i "%%a"=="--realtime" set REALTIME=1
)
if not defined REALTIME set NEXUS_TIME_SCALE=10
npx playwright test
exit /b %errorlevel%
