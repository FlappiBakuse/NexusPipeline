import assert from 'node:assert/strict';
import { createStore } from '../../apps/web/src/app/store';
import { ControlClient } from '../../apps/web/src/control/client';
import { AutomationCommands, automationSession } from '../../apps/web/src/pages/automation-editor';
import { useSettingsSession } from '../../apps/web/src/pages/settings-session';
import { PluginOperations } from '../../apps/web/src/pages/plugin-operations';

export async function automationRefreshCases(): Promise<string[]> {
  const measurements: string[] = [];
  for (const mode of ['save', 'move'] as const) {
    const fixture = controlledHost();
    const { store } = fixture;
    try {
      await store.navigate('users', true, fixture.access, undefined, fixture.snapshot());
      await fixture.streamOpened;
      fixture.reads.length = 0;
      const command = store.command();
      const user = { id: 'account', name: 'Saved account', order: mode === 'move' ? 2 : 1 };
      command.users.push({ expectedRevision: 1, value: user });
      const actions = new AutomationCommands(store, automationSession(store, 'users'), () => {});
      const result = actions.commit({ id: command.operationId, action: mode, target: user.id, kind: 'users', body: command });
      await fixture.catalogRequested;
      if (mode === 'move') {
        fixture.replyToSave();
        await new Promise<void>(resolve => setImmediate(resolve));
      }
      fixture.replyToCatalog();
      assert(await result);
      assert.equal(store.state.automation?.catalogRevision, 2);
      assert.deepEqual(store.state.automation?.bundle.users[0], user);
      assert.equal(fixture.writes(), 1);
      measurements.push(`${mode}: ${fixture.reads.length} GET, ${fixture.reads.reduce((sum, item) => sum + item.bytes, 0)} response bytes`);
    } finally { store.dispose(); }
  }
  assert(measurements.every(value => value.includes('1 GET,')), measurements.join('; '));
  return measurements.map(value => 'automation receipt refreshes the catalog and joins SSE when already pending: ' + value);
}

export async function settingsRefreshCases(): Promise<string[]> {
  const measurements: string[] = [];
  for (const mode of ['ordinary', 'startup-effect'] as const) {
    const fixture = controlledHost();
    const { store } = fixture;
    try {
      await store.navigate('users', true, fixture.access, undefined, fixture.snapshot());
      await fixture.streamOpened;
      fixture.reads.length = 0;
      const owner = useSettingsSession(store, () => true);
      owner.patch(mode === 'ordinary' ? { historyRetentionDays: 60 } : { autoStart: true });
      await fixture.settingsRequested;
      fixture.replyToSave();
      await new Promise<void>(resolve => setImmediate(resolve));
      fixture.replyToSettings();
      for (let attempt = 0; owner.session.busy && attempt < 10; attempt++) await new Promise<void>(resolve => setImmediate(resolve));
      assert.equal(owner.session.busy, false);
      assert.equal(owner.session.error, '');
      assert.equal(store.state.settings?.saved.revision, 2);
      assert.equal(store.state.settings?.effective.autoStart, mode === 'startup-effect');
      assert.equal(store.state.settings?.effective.historyRetentionDays, mode === 'ordinary' ? 60 : 30);
      assert.equal(fixture.writes(), 1);
      measurements.push(`${mode}: ${fixture.reads.length} GET, ${fixture.reads.reduce((sum, item) => sum + item.bytes, 0)} response bytes`);
    } finally { store.dispose(); }
  }
  assert(measurements[0]?.includes('1 GET,') && measurements[1]?.includes('2 GET,'), measurements.join('; '));
  return measurements.map(value => 'settings receipt shares current runtime reads and freshly verifies native effects: ' + value);
}

export async function pluginRefreshCases(): Promise<string[]> {
  const fixture = controlledHost(true), { store } = fixture;
  try {
    await store.navigate('users', true, fixture.access, undefined, fixture.snapshot());
    await fixture.streamOpened;
    fixture.reads.length = 0;
    const owner = new PluginOperations(store);
    const directory = await owner.enable(fixture.extension);
    assert.equal(owner.state.pending, null);
    assert.equal(store.state.settings?.saved.values.extensions[0]?.enabled, false);
    assert.equal(fixture.writes(), 2);
    const measured = `${fixture.reads.length} GET, ${fixture.reads.reduce((sum, item) => sum + item.bytes, 0)} response bytes`;
    assert.equal(fixture.reads.length, 2, measured);
    assert.deepEqual(fixture.reads.map(item => item.path).sort(), ['/control/v1/extensions', '/control/v1/settings']);
    assert.equal(directory?.items[0]?.activationId, null);
    return ['plugin enable confirmation returns the exact runtime directory for the page: ' + measured];
  } finally { store.dispose(); }
}

function controlledHost(plugins = false) {
  const instanceId = 'fixture', streamId = 'control-stream';
  const access = { instanceId, hostVersion: '0.18.0', capabilities: ['automation.write', 'settings.write', 'extensions.write'] };
  const snapshot = () => ({ instanceId, streamId, sequence: revision + settingsRevision - 1,
    state: { catalogRevision: revision, settingsRevision, interfaceRevision: 1, mutationSequence: 1 } });
  const interfaceState = { revision: 1, operationId: 'appearance', values: { theme: 'system', locale: 'zh-CN',
    dashboardCards: [], pinnedSettings: [], collapsedSettings: [] } };
  const values = { autoStart: false, desktop: { mode: 'browser', minimizeToTray: true, openOnStartup: false },
    service: { webPort: 0, allowRemoteAccess: false, mcpEnabled: false, mcpWriteEnabled: false, mcpPort: 18291 },
    allowConfigRepair: false, historyRetentionDays: 30, logLevel: 'info', locale: 'zhCn', proxy: { mode: 'none', url: null, userName: '' },
    updates: { checkEnabled: false, autoApplyEnabled: false, pluginAutoUpdateEnabled: false, channel: 'stable', sourceUrl: null, pluginSource: 'develop' },
    extensions: plugins ? [{ extensionId: 'sample', enabled: true }] : [], automaticRetry: { enabled: false, backoffSeconds: 5 } };
  const status = { instanceId, processId: 1, hostVersion: '0.18.0', admission: null, currentWebPort: 1234,
    pendingOperations: 0, execution: {}, scheduling: {} };
  const setup = Promise.withResolvers<void>(), requested = Promise.withResolvers<void>();
  const save = Promise.withResolvers<void>(), catalog = Promise.withResolvers<void>();
  const settingsWait = Promise.withResolvers<void>(), settingsRead = Promise.withResolvers<void>();
  const reads: { path: string; bytes: number }[] = [];
  let stream!: ReadableStreamDefaultController<Uint8Array>, revision = 1, writes = 0;
  let settingsRevision = 1, saved = values, effective = values;
  let user = { id: 'account', name: 'Initial account', order: 0 };
  const extension = { extensionId: 'sample', installedRevision: 1, isInstalled: true, desiredEnabled: true,
    activationId: 'sample-activation' as string | null, unavailableReason: null, capabilities: [] };
  const notify = () => {
    const event = snapshot();
    stream.enqueue(new TextEncoder().encode('data: ' + JSON.stringify({ format: 'nexus.event', version: 1, kind: 'state',
      topic: 'host.state', instanceId, streamId, sequence: event.sequence, subject: { type: 'instance', id: instanceId }, data: event.state }) + '\n\n'));
  };
  const response = (path: string, value: unknown) => {
    const json = JSON.stringify(value);
    reads.push({ path, bytes: new TextEncoder().encode(json).length });
    return new Response(json);
  };
  const client = new ControlClient('http://127.0.0.1', async (input, init) => {
    const path = new URL(String(input)).pathname;
    switch (path) {
      case '/control/v1/events/snapshot': return response(path, snapshot());
      case '/control/v1/events': return new Response(new ReadableStream<Uint8Array>({ start(controller) {
        stream = controller;
        init?.signal?.addEventListener('abort', () => controller.close(), { once: true });
        setup.resolve();
      } }));
      case '/control/v1/settings': {
        if (init?.method === 'POST') {
          const body = JSON.parse(String(init.body));
          assert.equal(body.expectedRevision, settingsRevision);
          writes++;
          saved = body.values;
          settingsRevision++;
          const startup = saved.autoStart !== effective.autoStart;
          if (!startup) effective = saved;
          if (!plugins) { notify(); await save.promise; }
          effective = saved;
          return Response.json({ operationId: body.operationId, saved: { revision: settingsRevision, values: saved },
            startupEffect: startup ? { operationId: body.operationId, state: 'completed', settingsRevision, enable: saved.autoStart } : null });
        }
        const runtime = { saved: { revision: settingsRevision, values: saved }, effective, restartReasons: [] };
        if (!plugins && settingsRevision > 1) { settingsWait.resolve(); await settingsRead.promise; }
        return response(path, runtime);
      }
      case '/control/v1/status': return response(path, status);
      case '/control/v1/settings/interface': return response(path, interfaceState);
      case '/control/v1/extensions': return response(path, { hasReleaseTrust: false, items: [extension] });
      case '/control/v1/extensions/reconcile': {
        const body = JSON.parse(String(init?.body));
        assert.equal(body.extensionId, extension.extensionId);
        assert.equal(body.expectedSettingsRevision, settingsRevision);
        assert.equal(body.expectedInstalledRevision, extension.installedRevision);
        writes++;
        extension.desiredEnabled = saved.extensions[0]!.enabled;
        extension.activationId = extension.desiredEnabled ? 'sample-activation' : null;
        return Response.json(extension);
      }
      case '/control/v1/automation': {
        if (revision > 1) { requested.resolve(); await catalog.promise; }
        return response(path, { catalogRevision: revision, roots: [], revisions: [{ kind: 'user', id: user.id, revision }],
          bundle: { format: 'nexus.automation.bundle', version: 1, exportKind: 'definitions', bundleId: 'test-bundle',
            createdAt: '2026-01-01T00:00:00Z', roots: [], scripts: [], users: [user], bindings: [], queues: [], schedules: [], attachments: [] } });
      }
      case '/control/v1/automation/change': {
        writes++;
        const body = JSON.parse(String(init?.body));
        assert.equal(body.expectedCatalogRevision, revision);
        user = body.users[0].value;
        revision++;
        if (user.order === 2) {
          notify();
          await save.promise;
        }
        return Response.json({ operationId: body.operationId, catalogRevision: revision, isNew: true });
      }
      default: throw new Error('Unexpected automation endpoint ' + path);
    }
  });
  return { store: createStore(client), access, snapshot, reads, extension, writes: () => writes, streamOpened: setup.promise,
    catalogRequested: requested.promise, replyToSave: save.resolve, replyToCatalog: catalog.resolve,
    settingsRequested: settingsWait.promise, replyToSettings: settingsRead.resolve };
}
