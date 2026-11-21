import { createApp, h, nextTick, ref } from 'vue';
import type { UiElement } from '@nexuspipeline/extension-sdk';
import { createStore } from '../../apps/web/src/app/store';
import { ControlClient } from '../../apps/web/src/control/client';
import { executionPreview } from '../../apps/web/src/control/execution';
import type { AutomationBundle } from '../../apps/web/src/control/wire.generated';
import ExecutionPlan from '../../apps/web/src/pages/ExecutionPlan.vue';
import SettingsDiagnostics from '../../apps/web/src/pages/SettingsDiagnostics.vue';
import { showsDetailsMotion } from './disclosure-motion';

type Check = (value: unknown, name: string) => void;

export async function planDiagnosticsContract(fixture: HTMLElement, check: Check): Promise<void> {
  await diagnosticsContract(fixture, check);
  await planContract(fixture, check);
}

async function diagnosticsContract(fixture: HTMLElement, check: Check): Promise<void> {
  const rows = [
    { id: 'host.version', status: 'pass', code: 'host.current', count: null },
    { id: 'host.install-write', status: 'pass', code: 'install.permission', count: null },
    { id: 'config.recovery', status: 'warn', code: 'config.inspect', count: 1 },
    { id: 'recovery.state', status: 'fail', code: 'recovery.inspect', count: 1 },
    { id: 'mcp.listener', status: 'notRequired', code: 'listener.disabled', count: null },
    { id: 'plugin.runtime', status: 'notRun', code: 'plugin.inspect', count: 0 }
  ];
  let requests = 0;
  const client = new ControlClient(location.origin, async input => {
    if (!String(input).endsWith('/control/v1/doctor')) throw Error('unexpected diagnostic request');
    requests++;
    if (requests === 2) return Response.json({ code: 'fixture_read_failed' }, { status: 503 });
    const checks = requests === 1 ? rows : rows.map(row => ({ ...row, count: null,
      status: requests === 3 && row.id === 'recovery.state' ? 'fail' : row.status === 'notRequired' ? row.status : 'pass' }));
    return Response.json({ admission: requests === 1 ? 'blocked' : requests === 3 ? 'changedDuringRead' : 'ready', isolatedResourceCount: 1,
      heldResourceCount: 0, pendingOperationCount: 0, capturedAtUtc: '2026-11-21T12:23:35Z',
      details: { frameworkVersion: '.NET 10', processArchitecture: 'X64', installedExtensionCount: 14, checks } });
  });
  const owner = createStore(client);
  const store = { ...owner, can: () => true };
  const mount = document.createElement('section'); mount.style.width = '700px'; fixture.append(mount);
  const app = createApp({ render: () => h(SettingsDiagnostics, { store, visible: true }) });
  const wait = async (ready: () => boolean) => {
    for (let count = 0; count < 100 && !ready(); count++) await new Promise(resolve => setTimeout(resolve, 10));
  };
  app.mount(mount);
  try {
    await wait(() => mount.innerText.includes('配置编辑与恢复'));
    const findings = () => [...mount.querySelectorAll<HTMLElement>('.diagnostic-finding')];
    check(findings().map(node => node.dataset.status).join(',') === 'fail,warn,notRun' &&
      findings()[0]?.innerText.includes('导出脱敏诊断包') && findings()[1]?.innerText.includes('保存或取消'),
      'diagnostics prioritize actionable findings, pair each with its own remedy, and separate unverified checks');
    const disclosures = [...mount.querySelectorAll('details')];
    check(disclosures.length === 3 && disclosures.every(detail => !detail.open) && !mount.innerText.includes('Host 版本'),
      'normal diagnostics are reachable through initially collapsed categories');
    const button = (label: string) => [...mount.querySelectorAll('button')].find(element => element.textContent?.trim() === label)!;
    const firstGroup = disclosures[0]!;
    const firstSummary = firstGroup.querySelector('summary')!;
    check(await showsDetailsMotion([firstGroup], () => firstSummary.click()), 'individual diagnostic categories animate open');
    check(await showsDetailsMotion([firstGroup], () => firstSummary.click()), 'individual diagnostic categories animate closed');
    check(await showsDetailsMotion(disclosures, () => button('展开全部').click()), 'all diagnostic groups animate open together');
    check(disclosures.every(detail => detail.open) && mount.innerText.includes('按条件跳过'), 'expanding all exposes the complete checks');
    check(disclosures[0]?.innerText.includes('创建、写入、同步和删除'), 'expanded healthy checks describe the scope of the actual probe');
    const detailRow = mount.querySelector<HTMLElement>('.diagnostic-check[data-status="fail"]')!;
    const columns = () => getComputedStyle(detailRow).gridTemplateColumns.split(' ').length;
    check(detailRow && columns() === 3 && detailRow.querySelector('.diagnostic-unit')?.textContent?.includes('1 项隔离或持有资源') &&
      detailRow.querySelector('.diagnostic-check-next')?.textContent?.includes('导出脱敏诊断包'),
      'desktop diagnostic details separate title, status and result with a compact unit and next step');
    mount.style.width = '372px';
    check(columns() === 2 && getComputedStyle(detailRow.querySelector('.diagnostic-check-body')!).gridColumnEnd === '-1' &&
      mount.scrollWidth <= mount.clientWidth + 1, 'narrow diagnostic details place the result under the label and status');
    check(await showsDetailsMotion(disclosures, () => button('收起全部').click()), 'all diagnostic groups animate closed together');
    check(disclosures.every(detail => !detail.open), 'collapsing all restores the condensed view');
    button('刷新诊断').click(); await wait(() => mount.innerText.includes('诊断读取失败'));
    check(findings().length === 3 && mount.innerText.includes('诊断读取失败'), 'a refresh failure leaves the prior report visible with a retry');
    button('重试').click(); await wait(() => findings().length === 1);
    check(findings()[0]?.innerText.includes('读取期间恢复状态发生变化') && findings()[0]?.innerText.includes('重新读取'),
      'a changing recovery revision calls for a fresh read without claiming an isolated resource');
    button('刷新诊断').click(); await wait(() => findings().length === 0);
    check(requests === 4 && findings().length === 0 && mount.innerText.includes('无需处理'), 'a healthy refresh replaces earlier findings');
  } finally { app.unmount(); mount.remove(); owner.dispose(); }
}

async function planContract(fixture: HTMLElement, check: Check): Promise<void> {
  const ready = 'execution_inputs_verified';
  const makeItem = (ordinal: number, scriptId: string, userId: string, reasonCode = ready) => ({
    ordinal, bindingId: scriptId + '-' + userId, scriptId, userId, disposition: reasonCode === ready ? 'runnable' : 'skipped', reasonCode,
    successfulRunsToday: Number(reasonCode === 'execution_daily_success_limit'), maximumSuccessfulRunsPerDay: scriptId === 'b' ? 1 : -1
  });
  const preview = executionPreview({ hasRunnable: true, hasBlockedItems: false,
    reasonCode: 'execution_has_runnable_items', completionAction: 'none',
    groups: ['a', 'b', 'c', 'a'].map(scriptId => ({ scriptId, reasonCode: ready })),
    items: [makeItem(0, 'a', 'main'), makeItem(1, 'a', 'alt'), makeItem(2, 'b', 'main'),
      makeItem(3, 'b', 'alt', 'execution_daily_success_limit'),
      makeItem(4, 'c', 'main', 'execution_queue_dependency_not_successful'), makeItem(5, 'a', 'main'), makeItem(6, 'a', 'alt')]
  });
  const bundle = { scripts: [{ id: 'a', name: '蔚蓝档案' }, { id: 'b', name: '原神' }, { id: 'c', name: '终末地' }],
    users: [{ id: 'main', name: '主号' }, { id: 'alt', name: '备用号' + 'LongName'.repeat(15) }],
    queues: [{ id: 'daily', scriptIds: ['a', 'b', 'c', 'a'], successDependencies: [{ scriptId: 'c', requiredScriptIds: ['b'] }] }]
  } as AutomationBundle;
  const mount = document.createElement('section'); mount.style.width = '700px'; fixture.append(mount);
  const current = ref(preview);
  const app = createApp({ render: () => h(ExecutionPlan, { preview: current.value, bundle, targetName: '日常', targetKind: 'queue', targetId: 'daily' }) });
  app.mount(mount);
  try {
    await nextTick();
    const steps = [...mount.querySelectorAll<HTMLElement>('.plan-step')];
    const accounts = [...mount.querySelectorAll<HTMLElement>('.plan-item')];
    const round = (step: HTMLElement | undefined) => step?.querySelector<UiElement<'nexus-badge'>>('h4 + nexus-badge')?.label;
    check(steps.length === 4 && accounts.length === 7 && accounts[0]?.dataset.bindingId === accounts[5]?.dataset.bindingId &&
      accounts.every((node, index) => Number(node.dataset.ordinal) === index) &&
      round(steps[0]) === '第 1 轮' && round(steps[3]) === '第 2 轮',
      'plan keeps the queue order, binding order and repeated script rounds');
    check(steps.filter(step => step.querySelector('.plan-dependency')).length === 1 &&
      steps[2]?.querySelector<HTMLElement>('.plan-dependency')?.innerText.includes('第 2 组') &&
      accounts[3]?.innerText.includes('1/1') && accounts[5]?.dataset.disposition === 'runnable',
      'only declared prerequisites get a dependency marker and later independent groups remain runnable');
    mount.style.width = '365px';
    check(mount.scrollWidth <= mount.clientWidth + 1 && accounts[3]?.innerText.includes('备用号'),
      'a narrow plan wraps long account names without horizontal overflow');
    current.value = { ...preview, hasBlockedItems: true,
      items: preview.items.map(item => item.ordinal === 0 ? { ...item, disposition: 'blocked', reasonCode: 'execution_capture_runtime_unbound' } : item) };
    await nextTick();
    check(mount.querySelector<UiElement<'nexus-badge'>>('.plan-overall')?.label === '无法运行' &&
      mount.querySelectorAll('[data-disposition="runnable"]').length > 0,
      'a globally blocked plan retains runnable account facts without presenting the selected plan as startable');
  } finally { app.unmount(); mount.remove(); }
}
