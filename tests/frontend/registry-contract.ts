import {
  contractFingerprint, sdkVersion, type ExtensionManifest, type FrontendExtension,
} from '@nexuspipeline/extension-sdk';
import { FrontendRegistry } from '../../apps/web/src/extensions/frontend-registry.js';
import { createApp, h, nextTick } from 'vue';
import SurfaceOutlet from '../../apps/web/src/extensions/SurfaceOutlet.vue';

function manifest(id: string): ExtensionManifest {
  return { format: 'nexus.extension.manifest', version: 1, extensionId: id, artifactName: 'Fixture', displayName: 'Fixture',
    packageVersion: '0.5.0', kind: 'managed-code', minHostVersion: '0.18.0', sdkVersion, contractFingerprint,
    capabilities: ['ui.contribute'], permissions: {},
    managed: { entryAssembly: 'Fixture.dll', entryType: 'Fixture.Extension' },
    frontend: { entry: 'web/main.js', styles: [], sdkVersion } };
}
function extension(id: string, activate: FrontendExtension['activate']): FrontendExtension {
  return { extensionId: id, sdkVersion, contractFingerprint, activate };
}
async function rejects(work: Promise<unknown>): Promise<boolean> {
  try { await work; return false; } catch { return true; }
}

export async function registryContracts(checks: string[]): Promise<void> {
  const assert = (condition: unknown, name: string): void => {
    if (!condition) throw new Error(name);
    checks.push(name);
  };
  const registry = new FrontendRegistry({ locale: 'en', appearance: 'dark', translate: (_id, key) => key });
  let rollbackCount = 0;
  const failed = extension('failed-fixture', async context => {
    context.registerStyle('body{--failed-fixture:1}');
    context.registerSurface({ id: 'card', target: 'overview.cards', mount: () => {} });
    context.registerRoute({ id: 'route', path: '/plugin/failed-fixture/tasks', label: 'Tasks', mount: () => {} });
    await context.scope.own(() => { rollbackCount++; });
    assert(registry.listRoutes().length === 0 && registry.listSurfaces('overview.cards').length === 0,
      'staged extension registrations are undiscoverable during activation');
    throw new Error('Controlled activation failure');
  });
  assert(await rejects(registry.activate(manifest(failed.extensionId), failed)), 'failed activation is reported');
  assert(rollbackCount === 1 && registry.listRoutes().length === 0 && registry.listSurfaces('overview.cards').length === 0
    && !document.querySelector('style[data-extension-id=failed-fixture]'), 'failed activation rolls back all resource kinds');

  const good = extension('good-fixture', context => {
    context.registerStyle('body{--good-fixture:1}');
    context.registerRoute({ id: 'tasks', path: '/plugin/good-fixture/tasks', label: 'Tasks', mount: () => {} });
    context.registerSurface({ id: 'preview', target: 'execution.running.preview', mount: surface => {
      assert(!surface.visible && !surface.element.isConnected, 'surface prepares detached with no visible work');
      const child = document.createElement('nexus-card');
      child.textContent = 'Fixture preview';
      surface.element.append(child);
      return () => { mountsDisposed++; };
    } });
  });
  let mountsDisposed = 0;
  const owner = await registry.activate(manifest(good.extensionId), good);
  const container = document.createElement('section');
  document.body.append(container);
  for (let index = 0; index < 100; index++) {
    const session = await registry.mount(good.extensionId, 'preview', container,
      { surfaceId: 'execution.running.preview', instanceId: 'fixture-instance', runId: 'fixture-run', attemptId: 'fixture-attempt' });
    session.setVisible(true);
    if (!session.visible) throw new Error('Connected surface did not become visible');
    await session.scope.dispose();
    if (container.childElementCount) throw new Error('Disposed surface left a connected candidate');
  }
  assert(mountsDisposed === 100, 'one hundred mount disposal cycles clean exactly once');
  assert(registry.listRoutes().length === 1 && registry.listSurfaces('execution.running.preview').length === 1,
    'successful activation commits routes and surfaces together');
  await owner.dispose();
  assert(registry.listRoutes().length === 0 && registry.listSurfaces('execution.running.preview').length === 0
    && !document.querySelector('style[data-extension-id=good-fixture]'), 'extension disposal withdraws every registration and style');
  container.remove();

  let resolveActivation!: () => void;
  let lateCleanup = 0;
  const cancellation = new AbortController();
  const pending = extension('pending-fixture', async () => {
    await new Promise<void>(resolve => { resolveActivation = resolve; });
    return () => { lateCleanup++; };
  });
  const activation = registry.activate(manifest(pending.extensionId), pending, cancellation.signal);
  cancellation.abort();
  resolveActivation();
  assert(await rejects(activation) && lateCleanup === 1, 'canceled activation cleans late completion without publication');

  const alreadyCancelled = new AbortController();
  alreadyCancelled.abort();
  let cancelledCalled = false;
  assert(await rejects(registry.activate(manifest('cancelled-fixture'), extension('cancelled-fixture', () => {
    cancelledCalled = true;
  }), alreadyCancelled.signal)) && !cancelledCalled, 'pre-cancelled extension never enters activation');

  const appearance = extension('appearance-fixture', context => {
    context.registerSurface({ id: 'theme', target: 'application.appearance', mount: () => {} });
    context.registerSurface({ id: 'other', target: 'overview.cards', mount: () => {} });
  });
  const appearanceOwner = await registry.activate(manifest(appearance.extensionId), appearance);
  const appearanceHost = document.createElement('section'); document.body.append(appearanceHost);
  const originalTheme = document.body.dataset.theme;
  const originalAccent = document.body.style.getPropertyValue('--accent');
  const originalPanel = document.body.style.getPropertyValue('--panel');
  document.body.dataset.theme = 'dark';
  const appearanceSession = await registry.mount(appearance.extensionId, 'theme', appearanceHost,
    { surfaceId: 'application.appearance', instanceId: 'fixture-instance' });
  appearanceSession.setAppearance({ accent: '#334455', accentStrong: '#445566', cardDark: '#112233',
    cardLight: '#eeeeee', cardOpacity: .6, secondaryTransparent: true });
  assert(document.body.style.getPropertyValue('--accent') === '#334455' &&
    document.body.style.getPropertyValue('--panel').includes('#112233') &&
    document.body.style.getPropertyValue('--panel-soft').includes('#112233'),
  'appearance applies validated accent, card opacity and secondary transparency');
  let invalidAppearance = false;
  try { appearanceSession.setAppearance({ accent: '#bad', extra: 'unsafe' } as never); }
  catch { invalidAppearance = true; }
  assert(invalidAppearance && document.body.style.getPropertyValue('--accent') === '#334455',
    'appearance rejects invalid or unknown fields without mutating current colors');
  document.body.dataset.theme = 'light';
  await new Promise(resolve => setTimeout(resolve, 0));
  assert(document.body.style.getPropertyValue('--panel').includes('#eeeeee'),
    'appearance chooses the light card color when Host theme changes');
  const otherSession = await registry.mount(appearance.extensionId, 'other', appearanceHost,
    { surfaceId: 'overview.cards', instanceId: 'fixture-instance' });
  let wrongSurface = false;
  try { otherSession.setAppearance({ accent: '#ffffff' }); } catch { wrongSurface = true; }
  assert(wrongSurface && document.body.style.getPropertyValue('--accent') === '#334455',
    'non-appearance surfaces cannot write Host appearance');
  await otherSession.scope.dispose();
  appearanceSession.setAppearance(null);
  assert(document.body.style.getPropertyValue('--accent') === originalAccent &&
    document.body.style.getPropertyValue('--panel') === originalPanel,
    'clearing appearance restores prior Host inline tokens');
  appearanceSession.setAppearance({ accent: '#abcdef', cardOpacity: .4 });
  await appearanceSession.scope.dispose();
  assert(document.body.style.getPropertyValue('--accent') === originalAccent &&
    document.body.style.getPropertyValue('--panel') === originalPanel,
    'appearance scope disposal restores Host tokens');
  let lateAppearance = false;
  try { appearanceSession.setAppearance({ accent: '#ffffff' }); } catch { lateAppearance = true; }
  assert(lateAppearance && document.body.style.getPropertyValue('--accent') === originalAccent,
    'late appearance writes from a disposed surface are rejected');
  await appearanceOwner.dispose(); appearanceHost.remove();
  if (originalTheme === undefined) delete document.body.dataset.theme;
  else document.body.dataset.theme = originalTheme;

  const invalids: ExtensionManifest[] = [
    { ...manifest('invalid-fixture'), version: 2 as 1 },
    { ...manifest('invalid-fixture'), capabilities: [] },
    { ...manifest('invalid-fixture'), contractFingerprint: '0'.repeat(64) },
  ];
  for (const value of invalids) {
    let called = false;
    assert(await rejects(registry.activate(value, extension(value.extensionId, () => { called = true; }))) && !called,
      'invalid frontend schema capability or fingerprint is rejected before activation');
  }
  let language = 'zh-CN', localeChanges = 0, mounted = 0;
  const localized = new FrontendRegistry({ get locale() { return language; }, appearance: 'dark',
    translate: (_id, key) => key });
  const localeOwner = await localized.activate(manifest('locale-fixture'), extension('locale-fixture', context => {
    context.registerRoute({ id: 'route', path: '/plugin/locale-fixture/tasks',
      get label() { return context.locale === 'en-US' ? 'Tasks' : '任务'; },
      mount: surface => {
        mounted++;
        const draft = document.createElement('textarea'), title = document.createElement('h2');
        draft.value = '{ invalid ordinary draft';
        const refresh = () => { localeChanges++; title.textContent = context.locale; };
        const stop = context.onLocaleChanged(refresh);
        surface.signal.addEventListener('abort', () => stop(), { once: true });
        surface.element.append(title, draft);
      } });
  }));
  const localeHost = document.createElement('section'); document.body.append(localeHost);
  const localeSession = await localized.mount('locale-fixture', 'route', localeHost,
    { surfaceId: 'application.navigation', instanceId: 'locale-instance' });
  const ordinaryDraft = localeHost.querySelector('textarea')!;
  ordinaryDraft.focus();
  language = 'en-US'; localized.refreshLocale();
  assert(localeHost.querySelector('h2')!.textContent === 'en-US' && localized.listRoutes()[0]!.label === 'Tasks',
    'activated extension context and navigation label observe locale changes');
  assert(mounted === 1 && ordinaryDraft.value === '{ invalid ordinary draft' && document.activeElement === ordinaryDraft,
    'locale notification preserves the mounted ordinary draft and focus');
  await localeSession.scope.dispose();
  language = 'zh-CN'; localized.refreshLocale();
  assert(localeChanges === 1, 'disposed surface cannot receive a later locale callback');
  await localeOwner.dispose(); localeHost.remove();
  await retainedOutlet(checks);
}

async function retainedOutlet(checks: string[]): Promise<void> {
  const registry = new FrontendRegistry({ locale: 'zh-CN', appearance: 'dark', translate: (_id, key) => key });
  let mounts = 0, disposed = 0;
  const card = extension('retained-fixture', context => {
    context.registerSurface({ id: 'card', target: 'overview.cards', mount: surface => {
      mounts++;
      surface.element.append(document.createElement('input'));
      return () => { disposed++; };
    } });
  });
  let owner = await registry.activate(manifest(card.extensionId), card);
  const host = document.createElement('section'); document.body.append(host);
  const app = createApp({ render: () => h(SurfaceOutlet, { registry, target: 'overview.cards',
    context: { surfaceId: 'overview.cards', instanceId: 'retained-instance' }, visible: true,
    extensionIds: ['retained-fixture'] }) });
  const flush = async () => { await nextTick(); await new Promise(resolve => setTimeout(resolve, 0)); };
  app.mount(host); await flush();
  const draft = host.querySelector('input')!;
  if (!draft) throw new Error('Retained surface did not mount');
  draft.value = 'unsaved draft';
  const unrelated = await registry.activate(manifest('unrelated-fixture'), extension('unrelated-fixture', context => {
    context.registerSurface({ id: 'other', target: 'overview.cards', mount: () => {} });
  }));
  await flush();
  if (host.querySelector('input') !== draft || draft.value !== 'unsaved draft' || mounts !== 1 || disposed !== 0)
    throw new Error('Unrelated activation replaced the selected surface and its draft');
  await unrelated.dispose(); await flush();
  if (host.querySelector('input') !== draft || mounts !== 1 || disposed !== 0)
    throw new Error('Unrelated retirement replaced the selected surface');
  checks.push('unrelated extension activation and retirement preserve the mounted surface and ordinary draft');
  await owner.dispose(); await flush();
  if (draft.isConnected || Number(disposed) !== 1) throw new Error('Selected extension retirement did not release its surface');
  owner = await registry.activate(manifest(card.extensionId), card); await flush();
  if (!host.querySelector('input') || host.querySelector('input') === draft || Number(mounts) !== 2)
    throw new Error('Selected extension reactivation did not mount its current contribution');
  checks.push('selected extension retirement and reactivation replace only its own surface');
  app.unmount(); await flush(); await owner.dispose(); host.remove();
}
