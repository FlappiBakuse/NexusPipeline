import assert from 'node:assert/strict';
import { createStore } from '../../apps/web/src/app/store';
import { ControlClient, ControlFailure } from '../../apps/web/src/control/client';
import type { ControlAutomationChange, ControlProjectCommand, AutomationScriptScript, ScriptSourceCandidate } from '../../apps/web/src/control/wire.generated';
import { AutomationCommands } from '../../apps/web/src/pages/automation-commands';
import { automationSession } from '../../apps/web/src/pages/automation-session';
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
      let revision = 1, completed = 0;
      const definitions: string[] = [], selections: string[] = [];
      store.refresh = async () => {
        store.state.automation!.catalogRevision = revision;
        store.state.automation!.revisions[0]!.revision = revision;
      };
      client.query = async (route, body, decode) => {
        assert.equal(route, '/control/v1/projects/context');
        assert.equal((body as ControlProjectCommand).scriptId, scriptId);
        return decode({ scriptId, extensionId: 'maaend', scriptRevision: revision, selectionRevision: 0, providerId: 'maa-framework' });
      };
      client.read = async (route, decode) => {
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
        await commands.check();
        assert.equal(definitions.length, 1);
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
    'uncertain source selection retries without a definition write', 'definitive source rejection permits a new save at the committed revision'];
}
