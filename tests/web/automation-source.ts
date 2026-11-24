import assert from 'node:assert/strict';
import { createStore } from '../../apps/web/src/app/store';
import { ControlClient, ControlFailure } from '../../apps/web/src/control/client';
import type { ControlAutomationChange, ControlAutomationImport, ControlProjectCommand, AutomationScriptScript, ScriptSourceCandidate } from '../../apps/web/src/control/wire.generated';
import { AutomationCommands, automationSession } from '../../apps/web/src/pages/automation-editor';
import { prepareScript } from '../../apps/web/src/pages/script-paths';
import { readSource, selectSource } from '../../apps/web/src/pages/script-source';

const scriptId = '5fa9f7c7-9f69-4aaa-8338-04cc2f0b47f3', directory = 'D:\\fixtures';
const candidate: ScriptSourceCandidate = {
  candidateId: 'account', title: 'Account', executable: 'app.exe', arguments: ['名字 "一"'],
  resources: [{ resourceId: 'main', path: 'config/account.json', syntax: 'json' },
    { resourceId: 'progress', path: 'state/progress.json', syntax: 'json' }],
  logPaths: ['logs/*.log'], projectSelection: { Source: { InstanceId: 'selected', Path: 'config/account.json' } }
};
const script: AutomationScriptScript = {
  id: scriptId, name: 'Example', kind: 'specialized', extensionId: 'maaend', rootId: 'root',
  executable: { rootId: 'root', relative: 'app.exe' }, arguments: [], configurationPaths: [], logPaths: [],
  judgement: { language: 'keywords', successKeywords: [], failureKeywords: [] },
  limits: { maximumAttempts: 1, stallTimeoutSeconds: 30, totalTimeoutSeconds: 60 },
  updateConfigurationAtFinish: false, adapterResources: []
};

async function templateReceiptCases(): Promise<string[]> {
  const cases: string[] = [];
  for (const mode of ['published', 'cancelled', 'accepted-then-published', 'lost-then-cancelled', 'quarantined'] as const) {
    const operationId = crypto.randomUUID(), key = 'template-draft';
    const body: ControlAutomationImport = { format: 'nexus.control.automation-import', version: 1, action: 'apply', operationId,
      expectedRevision: 1, previewDigest: 'a'.repeat(64), roots: [], attachments: [],
      bundle: { format: 'nexus.automation.bundle', version: 1, exportKind: 'scriptTemplate', bundleId: operationId,
        createdAt: '2026-01-01T00:00:00Z', scripts: [script], users: [], bindings: [], roots: [], queues: [], schedules: [], attachments: [] } };
    const submits: string[] = [], lookups: string[] = [], refreshed: number[] = [];
    const client = new ControlClient('http://127.0.0.1', async (input, init) => {
      const path = new URL(String(input)).pathname;
      if (init?.method === 'POST') {
        assert.equal(path, '/control/v1/automation/import/apply');
        submits.push(String(init.body));
        const first = submits.length === 1;
        if (mode === 'lost-then-cancelled' && first) throw new TypeError('fixture lost response');
        const state = mode === 'accepted-then-published' ? first ? 'accepted' : 'published' :
          mode === 'lost-then-cancelled' ? 'cancelled' : mode;
        const catalogRevision = state === 'published' ? 2 : state === 'cancelled' ? 1 : null;
        return Response.json({ operationId, state, catalogRevision, isNew: first });
      }
      assert.equal(path, '/control/v1/operations/' + operationId);
      lookups.push(path);
      return Response.json({ operationId, kind: 'automation.import', status: 'completed' });
    });
    const store = createStore(client);
    try {
      store.state.capabilities = { instanceId: 'fixture', hostVersion: 'fixture', capabilities: ['operations.read'] };
      store.drafts.set(key, { name: 'Original draft' });
      store.refreshCatalog = async accepted => { refreshed.push(accepted); };
      const session = automationSession(store, 'scripts'), commands = new AutomationCommands(store, session, () => {});
      const result = await commands.commit({ id: operationId, body, action: 'import', key, target: scriptId, kind: 'scripts' });
      const replay = mode === 'accepted-then-published' || mode === 'lost-then-cancelled';
      if (replay) {
        assert.equal(result, null);
        assert.equal(session.pending?.id, operationId);
        assert(store.drafts.has(key));
        // A completed operation can still be a cancelled publication, even when a later edit has
        // independently reached the expected next revision.
        store.state.automation = { catalogRevision: 8 } as never;
        await commands.check();
        assert.deepEqual(submits, [JSON.stringify(body), JSON.stringify(body)]);
        assert.equal(lookups.length, 1);
      } else assert.equal(submits.length, 1);
      const published = mode === 'published' || mode === 'accepted-then-published';
      assert.deepEqual(refreshed, published ? [2] : []);
      assert.equal(store.drafts.has(key), !published);
      assert.equal(!!session.pending, mode === 'quarantined');
      cases.push('template import distinguishes its published receipt from a completed or isolated operation: ' + mode);
    } finally { store.dispose(); }
  }
  return cases;
}

export async function sourceCases(): Promise<string[]> {
  const discovery = { format: 'nexus.script.source', version: 1, direction: 'response', operationId: scriptId,
    extensionId: 'maaend', candidates: [candidate], diagnostics: [] };
  const response = { rootPath: directory, installedRevision: 3, discovery };
  assert.deepEqual(readSource(response, scriptId, 'maaend', directory, 3), discovery);
  assert.throws(() => readSource(response, scriptId, 'maaend', 'D:\\other', 3), /script_source_response_invalid/);
  assert.throws(() => readSource(response, scriptId, 'maaend', directory, 4), /script_source_response_invalid/);
  for (const uncertain of [true, false]) {
    const client = new ControlClient('http://127.0.0.1');
    const store = createStore(client);
    try {
      store.state.capabilities = { instanceId: 'fixture', hostVersion: 'fixture', capabilities: ['configuration.edit', 'operations.read'] };
      store.state.automation = { catalogRevision: 1, roots: [{ id: 'root', revision: 1, path: directory }],
        revisions: [{ kind: 'script', id: scriptId, revision: 1 }],
        bundle: { format: 'nexus.automation.bundle', version: 1, bundleId: scriptId, createdAt: '2026-01-01T00:00:00Z',
          exportKind: 'definitions', roots: [{ id: 'root', kind: 'script', name: 'Root' }],
          scripts: [script], users: [], bindings: [], queues: [], schedules: [], attachments: [] } };
      const prepared = prepareScript(store, selectSource(store, script, candidate)).value;
      if (uncertain) {
        assert.deepEqual(prepared.configurationPaths.map(item => item.relative), ['config/account.json', 'state/progress.json']);
        assert.deepEqual(prepared.adapterResources.map(item => [item.resourceId, item.declarationIndex]), [['main', 0], ['progress', 1]]);
        assert.deepEqual(prepared.arguments, ['名字 "一"']);
      }
      const session = automationSession(store, 'scripts'), key = 'editor';
      store.drafts.set(key, { ...prepared });
      session.catalogs[key] = session.revisions[key] = 1;
      let revision = 1, completed = 0, lookups = 0, refreshFailure = uncertain;
      const definitions: string[] = [], selections: string[] = [];
      store.refreshCatalog = async () => {
        if (refreshFailure) throw new Error('snapshots_unavailable');
        store.state.automation!.catalogRevision = revision;
        store.state.automation!.revisions[0]!.revision = revision;
      };
      client.query = async (route, body, decode) => {
        assert.equal(route, '/control/v1/projects/context');
        assert.equal((body as ControlProjectCommand).scriptId, scriptId);
        return decode({ scriptId, extensionId: 'maaend', scriptRevision: revision, selectionRevision: 0, providerId: 'maa-framework' });
      };
      client.read = async (route, decode) => {
        lookups++;
        assert.equal(route, '/control/v1/operations/' + definitions[0]);
        return decode({ operationId: definitions[0], kind: 'automation.change', status: 'completed' });
      };
      client.write = async (route, body, decode) => {
        if (route === '/control/v1/automation/change') {
          const definition = body as ControlAutomationChange;
          assert.equal(definition.expectedCatalogRevision, revision);
          assert.equal(definition.scripts[0]?.expectedRevision, revision);
          definitions.push(definition.operationId);
          return decode({ operationId: definition.operationId, catalogRevision: ++revision, isNew: true });
        }
        assert.equal(route, '/control/v1/projects/select');
        const selection = body as ControlProjectCommand;
        assert.equal(selection.expectedScriptRevision, revision);
        assert.deepEqual(selection.selection, candidate.projectSelection);
        assert(selection.operationId);
        selections.push(selection.operationId);
        if (selections.length === 1)
          throw new ControlFailure(uncertain ? 503 : 409, 'controlled', undefined, selection.operationId, uncertain);
        return decode({ operationId: selection.operationId, revision: 1, isNew: true });
      };
      const commands = new AutomationCommands(store, session, () => completed++);
      const save = () => {
        const body = { ...store.command(), expectedCatalogRevision: session.catalogs[key]!,
          scripts: [{ value: prepared, expectedRevision: session.revisions[key]! }] };
        return commands.commit({ id: body.operationId, action: 'save', kind: 'scripts', target: scriptId, key, body });
      };
      await save();
      assert.equal(completed, 0);
      assert(store.drafts.has(key));
      if (uncertain) {
        assert.equal(session.pending?.id, definitions[0]);
        assert.equal(session.pending?.sourcePending, true);
        assert.equal(session.error, '定义已提交，所选配置等待处理。');
        assert.deepEqual(selections, []);
        const capabilities = store.state.capabilities!;
        store.state.capabilities = { ...capabilities, capabilities: ['configuration.edit'] };
        refreshFailure = false;
        await commands.check();
        assert.equal(lookups, 0);
        assert.deepEqual(selections, []);
        store.state.capabilities = capabilities;
        await commands.check();
        assert.equal(session.error, '定义已提交，所选配置结果待确认。');
        await commands.check();
        assert.equal(definitions.length, 1);
        assert.equal(lookups, 2);
        assert.deepEqual(selections, [selections[0], selections[0]]);
      } else {
        assert.equal(session.pending, null);
        await save();
        assert.equal(definitions.length, 2);
        assert.equal(selections.length, 2);
        assert.notEqual(selections[0], selections[1]);
      }
      assert.equal(completed, 1);
      assert.equal(session.pending, null);
      assert(!store.drafts.has(key));
    } finally { store.dispose(); }
  }
  return ['source choices bind all saved paths and preserve quoted arguments', 'source responses reject stale directories and revisions',
    'uncertain source selection retries without a definition write', 'definitive source rejection permits a new save at the committed revision',
    ...await templateReceiptCases()];
}
