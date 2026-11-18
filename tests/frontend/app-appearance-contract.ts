import { createApp, h, nextTick } from 'vue';
import { useAppAppearance } from '../../apps/web/src/app/app-appearance';
import { createStore } from '../../apps/web/src/app/store';
import { locale } from '../../apps/web/src/app/locale';
import { ControlClient } from '../../apps/web/src/control/client';
import type { InterfaceSnapshot } from '../../apps/web/src/control/interface';

type Check = (value: unknown, name: string) => void;
type Change = { operationId: string; expectedRevision: number; values: InterfaceSnapshot['values'] };
type Mode = 'saved' | 'denied' | 'failed' | 'conflict' | 'unknown';
const initial = (): InterfaceSnapshot => ({ revision: 0, operationId: 'c0000000-0000-4000-8000-000000000000',
  values: { theme: 'dark', locale: 'zh-CN', dashboardCards: ['status'], pinnedSettings: [], collapsedSettings: [] } });

function mountAppearance(mode: Mode) {
  let snapshot = initial(), submitted: Change | undefined;
  const writes: Change[] = [], reads: (() => void)[] = [];
  const client = new ControlClient(location.origin, async (_url, request) => {
    if (request?.method === 'POST') {
      const change = JSON.parse(String(request.body)) as Change;
      writes.push(change); submitted = change;
      if (mode === 'denied' || mode === 'failed')
        return Response.json({ code: mode === 'denied' ? 'permission_revoked' : 'controlled_settings_failure' },
          { status: mode === 'denied' ? 403 : 500 });
      if (mode === 'conflict') {
        snapshot = { ...snapshot, revision: 1, values: { ...snapshot.values, theme: 'light' } };
        return Response.json({ code: 'revision_conflict' }, { status: 409 });
      }
      snapshot = { revision: change.expectedRevision + 1, operationId: change.operationId, values: change.values };
      return Response.json(mode === 'unknown' ? { reply: 'lost' } : snapshot);
    }
    if (mode === 'unknown') await new Promise<void>(resolve => { reads.push(resolve); });
    return Response.json(snapshot);
  });
  const store = createStore(client);
  store.state.interface = initial();
  store.state.capabilities = { instanceId: 'appearance-fixture', hostVersion: '0.18.0', capabilities: ['settings.write'] };
  const holder = document.createElement('section'); document.body.append(holder);
  let appearance!: ReturnType<typeof useAppAppearance>;
  const app = createApp({ setup() {
    appearance = useAppAppearance(store);
    return () => h('div', [h('button', { type: 'button', disabled: appearance.themeBusy.value,
      onClick: appearance.toggleTheme }, 'Theme'),
      h('output', { class: 'theme-notice' }, appearance.themeNotice.value),
      h('output', { class: 'theme-error' }, store.state.error)]);
  } });
  app.mount(holder);
  return { store, holder, writes, reads, get appearance() { return appearance; },
    notice: () => holder.querySelector('.theme-notice')!.textContent!,
    failure: () => holder.querySelector('.theme-error')!.textContent!,
    click: () => holder.querySelector('button')!.click(),
    setMode(value: Mode) { mode = value; },
    unrelatedProof() { snapshot = { ...snapshot, operationId: initial().operationId }; },
    originalProof() { snapshot = { ...snapshot, operationId: submitted!.operationId }; },
    dispose() { app.unmount(); holder.remove(); store.dispose(); } };
}

async function until(condition: () => boolean): Promise<void> {
  const deadline = performance.now() + 5000;
  while (!condition()) {
    if (performance.now() > deadline) throw new Error('Theme fixture did not reach the expected state');
    await new Promise(resolve => setTimeout(resolve, 0));
  }
  await nextTick();
}

export async function appAppearanceContract(check: Check): Promise<void> {
  const previousTheme = document.body.dataset.theme, previousLanguage = document.documentElement.lang, previousLocale = locale.value;
  try {
    for (const mode of ['denied', 'failed', 'conflict'] as const) {
      const fixture = mountAppearance(mode);
      try {
        fixture.click(); await until(() => !fixture.appearance.themeBusy.value);
        check(fixture.notice() === '', mode + ' theme write never announces an unconfirmed theme');
        check(!!fixture.failure(), mode + ' theme write exposes its failure through the application error owner');
        const expected = mode === 'conflict' ? 'light' : 'dark';
        check(fixture.store.state.interface?.values.theme === expected && document.body.dataset.theme === expected,
          mode + ' theme write keeps the authoritative theme visible');
        check(fixture.writes.length === 1, mode + ' theme write is not replayed after failure');
      } finally { fixture.dispose(); }
    }
    await confirmedCycle(check);
    await unknownProof(check);
  } finally {
    if (previousTheme === undefined) delete document.body.dataset.theme;
    else document.body.dataset.theme = previousTheme;
    locale.value = previousLocale;
    document.documentElement.lang = previousLanguage;
  }
}

async function confirmedCycle(check: Check): Promise<void> {
  const fixture = mountAppearance('saved');
  try {
    for (const [theme, notice] of [['system', '跟随系统'], ['light', '浅色'], ['dark', '深色']] as const) {
      const count = fixture.writes.length;
      fixture.click(); await until(() => !fixture.appearance.themeBusy.value);
      check(fixture.store.state.interface?.values.theme === theme && fixture.notice() === '主题：' + notice,
        'confirmed ' + theme + ' theme is announced from the saved preference');
      check(fixture.writes.length === count + 1 && fixture.failure() === '',
        'normal ' + theme + ' theme toggle submits once without reporting failure');
    }
    fixture.setMode('denied'); fixture.click(); await until(() => !fixture.appearance.themeBusy.value);
    check(fixture.notice() === '' && !!fixture.failure(), 'a failed later toggle clears the previous theme notice');
  } finally { fixture.dispose(); }
}

async function unknownProof(check: Check): Promise<void> {
  const fixture = mountAppearance('unknown');
  try {
    fixture.click(); await until(() => fixture.reads.length === 1);
    const original = fixture.writes[0].operationId;
    check(fixture.notice() === '' && fixture.appearance.themeBusy.value,
      'a lost theme write reply shows no confirmation while its original operation is being checked');
    fixture.unrelatedProof(); fixture.reads.shift()!();
    await until(() => fixture.reads.length === 1);
    check(fixture.notice() === '' && fixture.store.interfaceChange.pending?.id === original,
      'equal theme values from another operation do not announce success or replace the original operation');
    fixture.originalProof(); fixture.reads.shift()!();
    await until(() => !fixture.appearance.themeBusy.value);
    check(fixture.notice() === '主题：跟随系统' && !fixture.store.interfaceChange.pending && fixture.failure() === '',
      'the exact original theme operation is announced after read-only confirmation');
    check(fixture.writes.length === 1, 'confirming an unknown theme write never submits it again');
  } finally { fixture.dispose(); }
}
