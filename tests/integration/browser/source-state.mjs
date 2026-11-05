import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import vm from 'node:vm';

const [root, modules, output] = process.argv.slice(2);
const startedAtUtc = new Date().toISOString(), started = performance.now();
if (![root, modules, output].every(value => value && path.isAbsolute(value)) || output.startsWith(root + path.sep))
  throw Error('Explicit source, dependencies and external output required');
const require = createRequire(path.join(modules, 'typescript/package.json'));
const ts = require(path.join(modules, 'typescript/lib/typescript.js'));
const loaded = new Map(), hashes = {}, checks = [];
const sha = file => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
function load(file) {
  file = path.resolve(file);
  if (!file.startsWith(path.join(root, 'apps/web/src') + path.sep)) throw Error('Foreign source import');
  if (loaded.has(file)) return loaded.get(file).exports;
  hashes[file] = sha(file);
  const module = { exports: {} }; loaded.set(file, module);
  const code = ts.transpileModule(fs.readFileSync(file, 'utf8'), {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS }
  }).outputText;
  const script = vm.runInThisContext('(function(require,module,exports){' + code + '\n})', { filename: file });
  script(name => {
    if (!name.startsWith('.')) throw Error('Unexpected external dependency');
    return load(path.resolve(path.dirname(file), name + '.ts'));
  }, module, module.exports);
  return module.exports;
}
const source = load(path.join(root, 'apps/web/src/pages/script-source.ts'));
const paths = load(path.join(root, 'apps/web/src/pages/script-paths.ts'));
const { AutomationCommands } = load(path.join(root, 'apps/web/src/pages/automation-commands.ts'));
const { ControlFailure } = load(path.join(root, 'apps/web/src/control/client.ts'));
const scriptId = '5fa9f7c7-9f69-4aaa-8338-04cc2f0b47f3';
const candidate = { candidateId: 'account', title: 'Account', executable: 'app.exe', arguments: ['名字 "一"'],
  resources: [{ resourceId: 'main', path: 'config/account.json', syntax: 'json' },
    { resourceId: 'progress', path: 'state/progress.json', syntax: 'json' }], logPaths: ['logs/*.log'],
  projectSelection: { Source: { InstanceId: 'selected', Path: 'config/account.json' } } };
function fixture() {
  const script = { id: scriptId, name: 'Example', kind: 'specialized', extensionId: 'maaend', rootId: 'root',
    executable: { rootId: 'root', relative: 'app.exe' }, arguments: [], configurationPaths: [], logPaths: [],
    judgement: { language: 'keywords' }, adapterResources: [] };
  const store = { state: { capabilities: { instanceId: 'instance' },
    automation: { catalogRevision: 1, roots: [{ id: 'root', path: 'D:\\fixtures' }],
      bundle: { roots: [{ id: 'root', kind: 'script' }] }, revisions: [{ kind: 'script', id: scriptId, revision: 1 }] } },
    drafts: new Map(), dirty: new Set(), can: () => true, report: () => {} };
  store.drafts.set('editor', script);
  return { store, script };
}
{
  const { store, script } = fixture();
  const selected = source.selectSource(store, script, candidate);
  const prepared = paths.prepareScript(store, selected).value;
  assert.equal(prepared.configurationPaths.length, prepared.adapterResources.length);
  assert.equal(prepared.configurationPaths[1].relative, 'state/progress.json');
  assert.deepEqual(prepared.arguments, candidate.arguments);
  assert.equal(store.drafts.get(source.scriptSourceKey(scriptId)).selection.Source.InstanceId, 'selected');
  checks.push('Chosen resources cover every saved path and preserve explicit instance and quoted arguments');
}
{
  const message = { format: 'nexus.script.source', version: 1, direction: 'response', operationId: scriptId,
    extensionId: 'maaend', candidates: [candidate], diagnostics: [] };
  const response = { rootPath: 'D:\\fixtures', installedRevision: 3, discovery: message };
  assert.equal(source.readSource(response, scriptId, 'maaend', response.rootPath, 3).candidates.length, 1);
  assert.throws(() => source.readSource(response, scriptId, 'maaend', 'D:\\other', 3));
  assert.throws(() => source.readSource(response, scriptId, 'maaend', response.rootPath, 4));
  checks.push('Delayed responses from another directory or plugin revision are rejected');
}
for (const uncertain of [true, false]) {
  const { store, script } = fixture(); source.selectSource(store, script, candidate);
  const session = { pending: null, saving: false, revisions: { editor: 1 }, catalogs: { editor: 1 }, error: '', notice: '' };
  let committed = 1, selectionAttempts = 0, completed = 0;
  const ids = [];
  store.refresh = async () => {
    store.state.automation.catalogRevision = committed;
    store.state.automation.revisions[0].revision = committed;
  };
  store.client = {
    query: async () => ({ scriptId, extensionId: 'maaend', scriptRevision: committed, selectionRevision: 0, providerId: 'maa-framework' }),
    read: async route => ({ operationId: route.split('/').at(-1), kind: 'automation.change', status: 'completed' }),
    write: async (route, body, decode) => {
      if (route.endsWith('/change')) {
        assert.equal(body.expectedCatalogRevision, committed); committed++;
        return decode({ operationId: body.operationId, catalogRevision: committed, isNew: true });
      }
      ids.push(body.operationId); selectionAttempts++;
      assert.equal(body.selection.Source.InstanceId, 'selected');
      if (selectionAttempts === 1) throw new ControlFailure(uncertain ? 503 : 409, 'controlled', undefined, body.operationId, uncertain);
      return decode({ operationId: body.operationId, revision: 1, isNew: true });
    }
  };
  const commands = new AutomationCommands(store, session, () => completed++);
  const action = revision => ({ id: crypto.randomUUID(), action: 'save', kind: 'scripts', target: scriptId, key: 'editor',
    body: { format: 'nexus.control.automation-change', version: 1, operationId: crypto.randomUUID(), expectedCatalogRevision: revision } });
  const first = action(1); first.body.operationId = first.id;
  await commands.commit(first);
  assert.equal(committed, 2); assert.equal(completed, 0); assert.ok(store.drafts.has('editor'));
  if (uncertain) {
    assert.ok(session.pending); await commands.check(); assert.equal(committed, 2); assert.equal(ids[0], ids[1]);
    checks.push('An uncertain selection response retries the same operation without another definition write');
  } else {
    assert.equal(session.pending, null); assert.equal(session.catalogs.editor, 2); assert.equal(session.revisions.editor, 2);
    const second = action(2); second.body.operationId = second.id;
    await commands.commit(second); assert.equal(committed, 3); assert.notEqual(ids[0], ids[1]);
    checks.push('A definitive selection rejection unlocks the retained draft at the committed definition revision');
  }
  assert.equal(completed, 1); assert.equal(session.pending, null); assert.equal(store.drafts.has('editor'), false);
}
for (const [file, digest] of Object.entries(hashes)) assert.equal(sha(file), digest);
fs.mkdirSync(path.dirname(output), { recursive: true });
fs.writeFileSync(output, JSON.stringify({ status: 'PASS', checks, sourceMode: 'local-uncommitted', hashes,
  command: [process.execPath, ...process.argv.slice(1)], exitCode: 0, elapsedSeconds: (performance.now() - started) / 1000,
  startedAtUtc, runtimeSha256: sha(process.execPath), runnerSha256: sha(process.argv[1]),
  scope: 'Actual frontend state modules with controlled transport; browser rendering is separate' }, null, 2) + '\n');
console.log(JSON.stringify({ status: 'PASS', cases: checks.length, output }));
