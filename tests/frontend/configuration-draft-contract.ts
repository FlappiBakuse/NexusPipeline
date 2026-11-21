import { createApp, h, ref } from 'vue';
import BindingTaskPlan from '../../apps/web/src/pages/BindingTaskPlan.vue';
import HistoryTasks from '../../apps/web/src/pages/HistoryTasks.vue';
import { locale } from '../../apps/web/src/app/locale';
import type { ActiveStep } from '../../apps/web/src/control/execution';
import theme from '../../apps/web/src/styles/theme.css?inline';
import uiTheme from '../../apps/web/src/ui/theme.css?inline';
import { prepareAppExit } from '../../apps/web/src/app/exit-preparation';
import { bindingEditorFixture, draftFixture, exitDecision, settle } from './configuration-draft-fixture';

type Check = (value: unknown, name: string) => void;
export async function configurationDraftContract(parent: HTMLElement, check: Check): Promise<void> {
  await bindingEditor(parent, check);
  await taskPlan(parent, check);
  await draftTaskPlan(parent, check);
  await historyTasks(parent, check);
  await exitReadiness(check);
}
async function bindingEditor(parent: HTMLElement, check: Check): Promise<void> {
  const fixture = bindingEditorFixture(parent);
  try {
    await settle();
    const modal = fixture.holder.querySelector('nexus-modal')!;
    check(modal.querySelector('p')?.textContent?.includes('主程序已启动') &&
      modal.querySelector('p')?.textContent?.includes('快照'),
      'a launched binding editor shows save and cancel instructions in the body');
    const actions = [...modal.querySelectorAll('button')];
    check(actions.map(button => button.textContent?.trim()).join(',') === '关闭,取消,保存' &&
      actions.every(button => getComputedStyle(button).minWidth === '80px'),
      'the three binding editor actions use the common modal width');
    actions[2]!.focus();
    await new Promise(resolve => setTimeout(resolve, 450));
    check(!modal.querySelector('nexus-tooltip'), 'the save action has no focus bubble');
    actions[0]!.click(); await settle();
    fixture.reopen(); await settle();
    check(fixture.writes.length === 1 && fixture.holder.textContent?.includes('主程序已启动'),
      'closing and reopening shows the retained editing session without launching a second writer');
    [...fixture.holder.querySelectorAll('button')].find(button => button.textContent?.trim() === '保存')!.click();
    await settle();
    check(fixture.writes.at(-1)?.endsWith('/session/save-binding') && !fixture.holder.querySelector('nexus-modal'),
      'save finishes the original session and closes the editor');
  } finally { fixture.dispose(); }
}
const firstTasks = [
  { taskId: 'one', title: 'Login', isSelected: true, isEnabled: true },
  { taskId: 'two', title: 'Unselected', titleLocales: { 'en-US': 'Localized task title', 'zh-CN': '未选任务' },
    isSelected: false, isEnabled: true },
  { taskId: 'three', title: 'Disabled', isSelected: false, isEnabled: false }
];

async function taskPlan(parent: HTMLElement, check: Check): Promise<void> {
  const bindingId = ref('binding');
  const visible = ref(true);
  const calls: Record<string, unknown>[][] = [];
  let repairs = 0;
  let mainRevision = 1;
  let isolated = false;
  let pluginEnabled = true;
  let finishStale: (() => void) | undefined;
  let staleSignal: AbortSignal | null | undefined;
  const fixture = draftFixture((route, options) => {
    if (route.endsWith('/extensions') && !pluginEnabled) return Response.json({ hasReleaseTrust: false, items: [] });
    if (route.endsWith('/resources')) {
      const id = route.split('/').at(-2);
      return Response.json({ bindingId: id, scriptId: 'script', bindingRevision: 2, scriptRevision: 3,
        kind: 'specialized', extensionId: 'fixture', resources: ['main', 'progress', 'software'].map((resourceId, index) =>
          ({ resourceId, label: resourceId, relativePath: resourceId + '.json', revision: index === 0 ? mainRevision : index + 1,
            declarationIndex: index, isSaved: id !== 'missing' || index !== 1, shape: 'file', fileCount: 1, syntax: 'json' })) });
    }
    if (route.endsWith('/propose')) {
      const { selection } = JSON.parse(String(options?.body));
      repairs++;
      return Response.json({ source: { bindingId: selection.bindingId, bindingRevision: 2, scriptRevision: 3,
        extensionId: 'fixture', installedRevision: 1, resources: selection.resources.map((resource: Record<string, unknown>) =>
          ({ ...resource, revision: resource.expectedRevision })) }, diagnostics: [], proposalDigest: 'a'.repeat(64), candidates: [] });
    }
    if (!route.endsWith('/discover')) return;
    const { bindingId: id, resources } = JSON.parse(String(options?.body)).selection;
    calls.push(resources);
    if (id === 'broken') return Response.json({ code: 'control_failed' }, { status: 500 });
    if (isolated) return Response.json({ code: 'configuration_recovery_pending' }, { status: 409 });
    const results = Response.json({ source: { bindingId: id, bindingRevision: 2, scriptRevision: 3,
      extensionId: 'fixture', installedRevision: 1, resources: resources.map((resource: Record<string, unknown>) =>
        ({ ...resource, revision: resource.expectedRevision })) }, tasks: firstTasks.map(task =>
      id === 'slow' ? { ...task, title: 'Stale probe' } : task),
    coverage: { selected: 1, projected: 1, retrySafe: 1, isComplete: true, hasBlockingDiagnostics: false },
    gamePathCheck: 'notApplicable', diagnostics: [] });
    if (id !== 'slow') return results;
    staleSignal = options?.signal;
    return new Promise<Response>(resolve => { finishStale = () => resolve(results); });
  });
  const holder = document.createElement('section'); parent.append(holder);
  const app = createApp({ render: () => h(BindingTaskPlan, { store: fixture.store, bindingId: bindingId.value, visible: visible.value }) });
  app.mount(holder);
  const originalLocale = locale.value;
  try {
    await settle();
    check(calls.length === 1 && JSON.stringify(calls[0]) === JSON.stringify([
      { resourceId: 'main', expectedRevision: 1, relativePath: 'main.json' },
      { resourceId: 'progress', expectedRevision: 2, relativePath: 'progress.json' },
      { resourceId: 'software', expectedRevision: 3, relativePath: 'software.json' }
    ]), 'an expanded saved binding probes each declared resource once, in declaration order');
    const diagnostics = holder.querySelector<HTMLDetailsElement>('.binding-task-plan details')!;
    check(!diagnostics.open && !holder.innerText.includes('main.json'), 'the task plan keeps full paths out of its initial view');
    const summary = diagnostics.querySelector('summary')!;
    summary.click();
    check(diagnostics.open && ['main.json', 'progress.json', 'software.json'].every(path => holder.innerText.includes(path)),
      'opening task diagnostics exposes every complete resource path');
    check(summary.innerText.includes('已启用业务任务 1 · 可安全重试 1') && !holder.innerText.includes('完整覆盖') &&
      !diagnostics.querySelector<HTMLElement>('.diagnostics-body')?.innerText.includes('可安全重试'),
      'diagnostics show task and retry counts once, in the disclosure summary');
    check(!holder.querySelector('input[type=checkbox]') &&
      [...holder.querySelectorAll('ol li')].map(card => card.querySelector('.task-number')?.textContent?.trim()).join(',') === '1,2,3',
      'the read-only source and every task keep their positions in the full saved plan');
    const decision = await exitDecision(fixture.store);
    check(!decision.prompted && decision.accepted && fixture.store.client.pendingWrites === 0 &&
      !holder.querySelector('nexus-toast.draft-restored-toast') && !holder.querySelector('[data-unsaved=true]'),
      'automatic probing leaves the parent draft and exit readiness intact');
    fixture.store.state.events = { mutationSequence: 1 }; await settle();
    check(calls.length === 1, 'an unrelated mutation retains the current task discovery');
    mainRevision++;
    fixture.store.state.events = { mutationSequence: 2 }; await settle();
    check(calls.length === 2 && calls.at(-1)?.[0]?.expectedRevision === 2, 'a saved resource revision change triggers one fresh probe');
    bindingId.value = 'slow'; await settle();
    check(!!finishStale && fixture.store.client.pendingWrites === 0, 'a probe in flight leaves mutation tracking untouched');
    bindingId.value = 'missing'; await settle();
    finishStale!(); await settle();
    check(calls.length === 3 && staleSignal?.aborted && !holder.textContent?.includes('Stale probe') &&
      !!holder.textContent?.includes('配置资源缺失'), 'switching bindings cancels an obsolete probe and a missing resource stops the entire selection');
    visible.value = false; await settle();
    bindingId.value = 'binding'; await settle();
    visible.value = true; await settle();
    check(calls.length === 4 && holder.textContent?.includes('Login'), 'reopening probes the current saved binding once');
    const findRepairs = () => [...holder.querySelectorAll('button')].find(button => button.textContent?.trim() === '查找修复候选')!;
    bindingId.value = 'broken'; await settle();
    check(!findRepairs().disabled && holder.textContent?.includes('读取任务计划失败'),
      'failed task discovery keeps independent repair lookup available with complete verified inputs');
    findRepairs().click(); await settle();
    check(repairs === 1 && holder.textContent?.includes('无候选修复'), 'repair candidates can be looked up after discovery failure');
    bindingId.value = 'binding'; await settle();
    isolated = true;
    fixture.store.state.events = { mutationSequence: 2, recoveryRevision: 1 }; await settle();
    check(findRepairs().disabled && holder.textContent?.includes('配置正在恢复') && !holder.textContent?.includes('Login'),
      'an opened binding entering isolation clears cards and blocks repair even at the same resource revisions');
    isolated = false;
    fixture.store.state.events = { mutationSequence: 2, recoveryRevision: 2 }; await settle();
    check(!findRepairs().disabled && holder.textContent?.includes('Login'), 'finishing recovery restores the binding’s current plan and repair entry');
    locale.value = 'en-US'; await settle();
    check(holder.textContent?.includes('Localized task title'), 'changing the UI language updates task titles through the frozen locale map');
    pluginEnabled = false;
    const count = calls.length;
    fixture.store.state.events = { mutationSequence: 2, recoveryRevision: 2, extensionGeneration: 'disabled' }; await settle();
    check(calls.length === count && !holder.textContent?.includes('Login') && holder.textContent?.includes('The extension is not installed or enabled'),
      'disabling the installed plugin clears existing tasks and skips a discovery command');
  } finally { locale.value = originalLocale; app.unmount(); holder.remove(); fixture.store.dispose(); }
}

async function draftTaskPlan(parent: HTMLElement, check: Check): Promise<void> {
  const candidateId = ref('first');
  const candidates: string[] = [];
  const fixture = draftFixture((route, options) => {
    if (!route.endsWith('/discover-draft')) return;
    const selection = JSON.parse(String(options?.body));
    candidates.push(selection.candidateId);
    return Response.json({ tasks: [{ ...firstTasks[0]!, title: selection.candidateId }] });
  });
  const holder = document.createElement('section'); parent.append(holder);
  const app = createApp({ render: () => h(BindingTaskPlan, { store: fixture.store, bindingId: 'draft-binding', visible: true,
    draft: { userId: 'account', scriptId: 'script', scriptRevision: 3, candidateId: candidateId.value }
  }) });
  app.mount(holder);
  try {
    await settle();
    candidateId.value = 'replacement';
    await settle();
    check(candidates.join(',') === 'first,replacement' && holder.textContent?.includes('replacement') &&
      holder.querySelector('nexus-badge')?.shadowRoot?.textContent?.includes('配置草稿') &&
      fixture.writes.every(route => route.endsWith('/discover-draft')),
      'only the current prepared draft is probed and labelled without saving the parent');
  } finally { app.unmount(); holder.remove(); fixture.store.dispose(); }
}

async function historyTasks(parent: HTMLElement, check: Check): Promise<void> {
  const title = 'A'.repeat(120);
  const steps: ActiveStep[] = [{ stepId: 'first', scriptId: 'script', userId: 'account-a', bindingId: 'binding',
    dependsOn: [], phase: 'completed', logRoles: [], tasks: [
      ...firstTasks.map((task, index) =>
        ({ ...task, title: index === 0 ? title : task.title, state: 'succeeded' as const, completionKind: 'completed' as const })),
      { ...firstTasks[0]!, taskId: 'satisfied', state: 'succeeded', completionKind: 'satisfied' },
      { ...firstTasks[0]!, taskId: 'failed', state: 'failed', reasonCode: 'adapter_task_missing_terminal' },
      { ...firstTasks[0]!, taskId: 'partial', state: 'partial' }
    ] },
  { stepId: 'second', scriptId: 'script', userId: 'account-b', bindingId: 'binding-b',
    dependsOn: [], phase: 'skipped', reasonCode: 'execution_cancel_requested', logRoles: [],
    tasks: [{ ...firstTasks[0]!, state: 'unknown', completionKind: 'notExecuted' }] },
  { stepId: 'third', scriptId: 'script', userId: 'account-b', bindingId: 'binding-b',
    dependsOn: [], phase: 'completed', reasonCode: 'execution_cancel_requested', logRoles: [],
    tasks: [{ ...firstTasks[0]!, state: 'unknown', reasonCode: 'baah_terminal_missing' },
      { ...firstTasks[0]!, taskId: 'cancelled', state: 'unknown' }] }];
  const holder = document.createElement('section'); holder.style.width = '700px'; parent.append(holder);
  const style = document.createElement('style');
  style.textContent = theme + '\n' + uiTheme;
  document.head.append(style);
  const before = document.body.dataset.theme;
  const display = { steps: [{ stepId: 'first', scriptName: 'Frozen Script' }, { stepId: 'second', scriptName: 'Frozen Script' }],
    accounts: [{ userId: 'account-a', name: 'First Account', stepIds: ['first'] },
      { userId: 'account-b', name: 'Second Account', stepIds: ['second'] }] };
  const app = createApp({ render: () => h(HistoryTasks, { steps, display }) });
  app.mount(holder);
  try {
    await settle();
    const groups = [...holder.querySelectorAll('ol')];
    check(groups.length === 3 && groups[0]?.getAttribute('aria-label') === 'Frozen Script · First Account' &&
      groups[1]?.getAttribute('aria-label') === 'Frozen Script · Second Account' && !holder.innerText.includes('Frozen Script') &&
      !holder.innerText.includes('First Account') && !holder.innerText.includes('Second Account') &&
      [...holder.querySelectorAll('.task-number')].map(item => item.textContent?.trim()).join(',') === '1,2,3,4,5,6,1,1,2',
      'history separates frozen steps and restarts the full task sequence in each step');
    const reasons = [...holder.querySelectorAll('ol article')].map(card => card.querySelector('p')?.textContent?.trim());
    check(reasons.every(Boolean) && reasons[4] === '未检测到任务终态' && reasons[6] === '运行已取消' &&
      reasons[7] === '未检测到任务终态' && reasons[0] !== reasons[1] && reasons[1] !== reasons[2],
      'every historical task displays its own short reason while keeping specific task and step evidence');
    const first = groups[0]!;
    const tile = first.querySelector('article')!;
    document.body.dataset.theme = 'dark';
    const dark = getComputedStyle(tile).backgroundColor;
    document.body.dataset.theme = 'light';
    check(dark !== getComputedStyle(tile).backgroundColor, 'the shared task tiles follow the installed light and dark surface colors');
    check(getComputedStyle(first).gridTemplateColumns.split(' ').length === 2 && first.scrollWidth <= first.clientWidth,
      'history cards use two tracks and wrap long localized titles inside a wide container');
    holder.style.width = '365px';
    check(getComputedStyle(first).gridTemplateColumns.split(' ').length === 1 && first.scrollWidth <= first.clientWidth,
      'the history task cards use a single track in a narrow container regardless of viewport');
  } finally {
    app.unmount(); holder.remove(); style.remove();
    if (before === undefined) delete document.body.dataset.theme;
    else document.body.dataset.theme = before;
  }
}
async function exitReadiness(check: Check): Promise<void> {
  const client = { pendingWrites: 0, canExitMaintenance: () => true };
  const decide = prepareAppExit(client);
  check(await decide('close') === 'ready' && !document.querySelector('nexus-confirm-dialog'),
    'normal exit readiness has no user decision stage');
  client.pendingWrites = 1;
  check(await decide('close') === 'pending-writes', 'actual in-flight writes remain an explicit exit blocker');
  check(await decide('maintenance') === 'ready', 'the signed restart handoff keeps its own waiting RPC exemption');
}
