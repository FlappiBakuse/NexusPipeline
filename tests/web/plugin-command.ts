import assert from 'node:assert/strict';
import { reactive } from 'vue';
import { ControlClient, ControlFailure } from '../../apps/web/src/control/client';
import { PluginOwner } from '../../apps/web/src/pages/plugin-owner';
import { savedSettings } from '../../apps/web/src/control/settings-command';
import type { AppStore } from '../../apps/web/src/app/store';
import type { ControlHostSettingsValues } from '../../apps/web/src/control/wire.generated';
import { upgradeAvailable, type CatalogEntry, type ExtensionCatalog, type ExtensionDetails } from '../../apps/web/src/control/extensions';

export const cases: string[] = [];
function check(value: unknown, name: string): void { assert(value, name); cases.push(name); }
const values: ControlHostSettingsValues = { autoStart: false, desktop: { mode: 'browser', minimizeToTray: true, openOnStartup: false },
  service: { webPort: 0, allowRemoteAccess: false, mcpEnabled: false, mcpWriteEnabled: false, mcpPort: 18291 },
  allowConfigRepair: false, historyRetentionDays: 30, logLevel: 'info', locale: 'zhCn', proxy: { mode: 'none', url: null, userName: '' },
  updates: { checkEnabled: false, autoApplyEnabled: false, pluginAutoUpdateEnabled: false, channel: 'stable', sourceUrl: null, pluginSource: 'develop' },
  extensions: [{ extensionId: 'sample', enabled: true }], automaticRetry: { enabled: false, backoffSeconds: 5 } };
const candidate: CatalogEntry = { extensionId: 'sample', artifactName: 'Sample', packageVersion: '0.5.0', kind: 'managed-code',
  minHostVersion: '0.18.0', sdkVersion: '0.1.0', artifactUrl: 'https://example.invalid/sample.zip', artifactBytes: 1,
  artifactSha256: 'a'.repeat(64), manifestSha256: 'b'.repeat(64) };
const catalog: ExtensionCatalog = { identity: { channel: 'develop', generationId: crypto.randomUUID(), sequence: 1, sha256: 'c'.repeat(64) },
  publishedAt: '2026-10-25T00:00:00Z', entries: [candidate] };
const json = (value: unknown): Response => new Response(JSON.stringify(value), { status: 200 });

function fixture() {
  let revision = 1, saved = structuredClone(values), writeId = '', malformed = false, wrongProof = false, wrongAction = false;
  let hold: (() => void) | undefined, held = false, reconcileUnknown = false, reconcileComplete = true;
  const requests: { path: string; body: any }[] = [];
  const installed = { extensionId: 'sample', installedRevision: 1, isInstalled: true, desiredEnabled: true,
    activationId: crypto.randomUUID() as string | null, unavailableReason: null, capabilities: [] as string[] };
  const client = new ControlClient('http://127.0.0.1', async (url, request) => {
    const path = new URL(String(url)).pathname, body = request?.body ? JSON.parse(String(request.body)) : undefined;
    if (request?.method === 'POST') requests.push({ path, body });
    if (path.endsWith('/extensions/apply')) {
      writeId = body.operationId;
      if (held) await new Promise<void>(resolve => { hold = resolve; });
      return json(malformed ? { operationId: writeId } : { operationId: writeId, journalId: 'journal', status: 'accepted' });
    }
    if (path.includes('/extensions/operations/')) return json({ operation: { operationId: wrongProof ? crypto.randomUUID() : writeId,
      kind: 'extension.apply', status: 'completed', initialJournalId: 'journal' }, extensionId: 'sample',
      action: wrongAction ? 'remove' : 'upgrade', phase: 'committed', outcome: 'installed', resultInstalledRevision: 2, resultIsInstalled: true });
    if (path === '/control/v1/settings') {
      writeId = body.operationId; revision++; saved = body.values; installed.desiredEnabled = saved.extensions[0]!.enabled;
      return json({ operationId: writeId, saved: { revision, values: malformed ? {} : saved } });
    }
    if (path.includes('/control/v1/operations/')) return json({ operationId: wrongProof ? crypto.randomUUID() : writeId,
      kind: 'settings.save', status: 'completed' });
    if (path.endsWith('/extensions/reconcile')) {
      if (reconcileComplete) { installed.desiredEnabled = saved.extensions[0]!.enabled; installed.activationId = installed.desiredEnabled ? 'active' : null; }
      if (reconcileUnknown) throw new TypeError('simulated transport failure');
      return json(installed);
    }
    if (path.endsWith('/extensions')) return json({ hasReleaseTrust: true, items: [installed] });
    throw new Error('Undeclared test path ' + path);
  });
  const state = reactive({ capabilities: { instanceId: 'instance-a' }, settings: { saved: { revision, values: structuredClone(saved) } },
    uncertainOperation: '', error: '' });
  const drafts = reactive(new Map()), dirty = reactive(new Set<string>());
  const store = { client, state, drafts, dirty, can: () => true, report: () => {}, refresh: async () => {
    state.settings = { saved: { revision, values: structuredClone(saved) } };
  } } as unknown as AppStore;
  return { store, requests, installed, mode: (value: { malformed?: boolean; wrongProof?: boolean; wrongAction?: boolean; held?: boolean;
    reconcileUnknown?: boolean; reconcileComplete?: boolean }) => {
    malformed = value.malformed ?? malformed; wrongProof = value.wrongProof ?? wrongProof; wrongAction = value.wrongAction ?? wrongAction;
    held = value.held ?? held; reconcileUnknown = value.reconcileUnknown ?? reconcileUnknown; reconcileComplete = value.reconcileComplete ?? reconcileComplete;
  }, release: () => hold!(), complete: () => { installed.desiredEnabled = saved.extensions[0]!.enabled; installed.activationId = installed.desiredEnabled ? 'active' : null; } };
}

export async function run(): Promise<string[]> {
  const details = { artifactSha256: 'd'.repeat(64), packageVersion: '0.5.0' } as ExtensionDetails;
  check(upgradeAvailable(details, candidate, 'develop') && !upgradeAvailable(details, candidate, 'stable')
    && !upgradeAvailable({ ...details, artifactSha256: candidate.artifactSha256 }, candidate, 'develop')
    && !upgradeAvailable({ ...details, packageVersion: '0.6.0' }, candidate, 'develop'),
    'Develop permits a different same-version artifact while stable, identical bytes and downgrades remain guarded');
  const body = { format: 'nexus.control.host-settings' as const, version: 1 as const, operationId: crypto.randomUUID(), expectedRevision: 1, values };
  assert.throws(() => savedSettings({ operationId: body.operationId, saved: { revision: 2, values: {} } }, body));
  check(savedSettings({ operationId: body.operationId, saved: { revision: 2, values: structuredClone(values) } }, body),
    'A settings write requires the complete original values, operation identity and next revision');
  const first = fixture(), owner = new PluginOwner(first.store); first.mode({ malformed: true, held: true });
  const work = owner.apply(candidate, 'upgrade', first.installed, catalog);
  check(owner.state.pending?.apply?.command.candidate && first.store.dirty.size === 1,
    'The complete extension command and catalog identity are owned before transport');
  const reopened = new PluginOwner(first.store);
  check(reopened.state === owner.state, 'Navigation remount shares the original instance-owned extension command');
  first.release(); await work;
  check(!!owner.state.pending && !owner.state.busy, 'Malformed acceptance retains its original operation without replay');
  await reopened.apply(candidate, 'upgrade', first.installed, catalog);
  check(first.requests.length === 1, 'An unresolved extension operation prevents a second installation command');
  first.mode({ wrongProof: true }); await reopened.inspect();
  check(!!owner.state.pending, 'A different completed extension operation cannot unlock the original command');
  first.mode({ wrongProof: false, wrongAction: true }); await reopened.inspect();
  check(!!owner.state.pending, 'The same ID with a different action is refused as completion proof');
  first.mode({ wrongAction: false }); await reopened.inspect();
  check(!owner.state.pending && first.requests.length === 1 && first.store.dirty.size === 0,
    'The exact original extension completion unlocks once without resending its write');
  const second = fixture(), settings = new PluginOwner(second.store); second.mode({ malformed: true });
  await settings.enable(second.installed);
  check(settings.state.pending?.kind === 'setting' && second.requests.length === 1,
    'A malformed settings success retains the original preference command before any activation');
  second.mode({ wrongProof: true }); await settings.inspect();
  check(settings.state.pending?.kind === 'setting', 'Another settings operation cannot authorize activation');
  second.mode({ wrongProof: false }); await settings.inspect();
  check(settings.state.pending?.kind === 'reconcile' && settings.state.retryable && second.requests.length === 1,
    'A proven saved preference reads runtime state without automatically repeating activation');
  await settings.retry();
  check(!settings.state.pending && second.requests.length === 2 && !second.installed.activationId,
    'Explicit retry applies only the frozen current preference and confirms the disabled runtime');
  const third = fixture(), runtime = new PluginOwner(third.store); third.mode({ reconcileUnknown: true });
  await runtime.enable(third.installed); await runtime.inspect();
  check(!runtime.state.pending && third.requests.length === 2,
    'A lost reconciliation response resolves through actual runtime state with no activation replay');
  const fourth = fixture(), switched = new PluginOwner(fourth.store); fourth.mode({ held: true });
  const delayed = switched.apply(candidate, 'upgrade', fourth.installed, catalog);
  fourth.store.state.capabilities!.instanceId = 'instance-b'; fourth.release(); await delayed;
  check(!new PluginOwner(fourth.store).state.pending, 'A late old-instance operation cannot publish pending state into another instance');
  const client = new ControlClient('http://127.0.0.1', async () => json({ operationId: body.operationId, saved: { revision: 2, values: {} } }));
  await assert.rejects(client.write('/control/v1/settings', body, value => savedSettings(value, body), body.operationId),
    (error: unknown) => error instanceof ControlFailure && error.uncertain && error.operationId === body.operationId);
  cases.push('Malformed successful settings transport remains uncertain with its original operation ID');
  return cases;
}
