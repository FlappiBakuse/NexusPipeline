'use strict';
const assert = require('node:assert/strict');
const test = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const typescript = require(process.env.NEXUS_FRONTEND_TYPESCRIPT);

function load(relative, dependencies, deadlines) {
  const source = fs.readFileSync(path.join(__dirname, '../../apps/web/src', relative), 'utf8');
  const compiled = typescript.transpileModule(source, { compilerOptions: { module: typescript.ModuleKind.CommonJS, target: typescript.ScriptTarget.ES2023 } });
  const sandbox = { exports: {}, URL, Headers, TextDecoder, Uint8Array, Date, AbortController,
    AbortSignal: { any: AbortSignal.any.bind(AbortSignal), timeout(milliseconds) {
      const controller = new AbortController();
      deadlines.push({ milliseconds, expire: () => controller.abort(new DOMException('Timed out', 'TimeoutError')) });
      return controller.signal;
    } },
    require(name) {
      if (Object.hasOwn(dependencies, name)) return dependencies[name];
      if (name.startsWith('.')) return load(path.join(path.dirname(relative), name) + '.ts', {}, deadlines);
      throw new Error('unexpected_fixture_dependency');
    } };
  vm.runInNewContext(compiled.outputText, sandbox); return sandbox.exports;
}

for (const ending of ['surface', 'request']) test('canceling the original ' + ending + ' stops an actual frontend write transport and retains its uncertain outcome', async () => {
  const deadlines = [];
  const session = load('control/frontend-session.ts', {}, deadlines);
  const clientModule = load('control/client.ts', { './frontend-session': session }, deadlines);
  const transportModule = load('extensions/frontend-transport.ts', { '../control/client': clientModule }, deadlines);
  const surface = new AbortController(), request = new AbortController();
  let entered; const started = new Promise(resolve => { entered = resolve; }); let networkSignal;
  const send = async (url, options) => {
    if (new URL(url).pathname === '/control/v1/extensions/clients') return new Response(JSON.stringify({
      clientId: '70000000-0000-4000-8000-000000000001', token: 'a'.repeat(64), expiresAt: new Date(Date.now() + 60000).toISOString() }));
    assert.equal(options.headers.get('X-Nexus-Frontend-Client'), 'a'.repeat(64));
    assert.equal(String(url).includes('a'.repeat(64)), false); networkSignal = options.signal; entered();
    return new Promise((_resolve, reject) => options.signal.addEventListener('abort', () => reject(options.signal.reason), { once: true }));
  };
  const client = new clientModule.ControlClient('http://fixture', send);
  const transport = new transportModule.InstalledTransport('local', 'fixture', 'activation', client, {}, surface.signal);
  const operation = '70000000-0000-4000-8000-000000000002';
  const pending = transport.invoke('login', {}, request.signal, operation); await started;
  assert.equal(client.pendingWrites, 1); assert.equal(networkSignal.aborted, false);
  (ending === 'surface' ? surface : request).abort();
  await assert.rejects(pending, error => error instanceof clientModule.ControlFailure && error.code === 'write_outcome_unknown' && error.operationId === operation && error.uncertain);
  assert.equal(networkSignal.aborted, true); assert.equal(client.pendingWrites, 0);
});

test('ordinary control write timeout cancels the network and retains an uncertain operation', { timeout: 5000 }, async () => {
  const deadlines = [];
  const session = load('control/frontend-session.ts', {}, deadlines);
  const { ControlClient } = load('control/client.ts', { './frontend-session': session }, deadlines);
  let signal, requests = 0;
  const client = new ControlClient('http://fixture', async (_url, options) => {
    requests++; signal = options.signal;
    return new Promise((_resolve, reject) => signal.addEventListener('abort', () => reject(signal.reason), { once: true }));
  });
  const operation = '70000000-0000-4000-8000-000000000003';
  const pending = client.write('/control/v1/settings', {}, value => value, operation);
  const rejected = assert.rejects(pending, error => error.code === 'write_outcome_unknown'
    && error.operationId === operation && error.uncertain);
  assert.equal(client.pendingWrites, 1);
  for (const deadline of deadlines) if (deadline.milliseconds <= 30000) deadline.expire();
  await rejected;
  assert.equal(signal.aborted, true); assert.equal(client.pendingWrites, 0); assert.equal(requests, 1);
});

for (const code of ['media_waiting_for_game', 'media_window_not_ready', 'media_emulator_not_ready', 'media_preview_unavailable',
  'access_denied', 'media_grant_closed', 'resource_unavailable']) test('preview maps only known readiness: ' + code, async () => {
  const deadlines = [];
  const session = load('control/frontend-session.ts', {}, deadlines);
  const clientModule = load('control/client.ts', { './frontend-session': session }, deadlines);
  const { InstalledTransport } = load('extensions/frontend-transport.ts', { '../control/client': clientModule }, deadlines);
  let closed = 0;
  const client = new clientModule.ControlClient('http://fixture', async url => {
    if (String(url).endsWith('/close')) { closed++; return new Response('{}'); }
    return new Response(JSON.stringify({ previewId: 'preview', runId: 'run', attemptId: 'attempt', source: 'pc_game' }));
  });
  const owner = new AbortController();
  const transport = new InstalledTransport('local', 'live-screenshot', 'activation', client,
    { acquire: async () => { throw new clientModule.ControlFailure(code === 'access_denied' ? 403 : 409, code); } }, owner.signal);
  const pending = transport.preview({ runId: 'run', attemptId: 'attempt' }, 'current-task', owner.signal);
  const states = { media_waiting_for_game: 'waiting_for_game', media_window_not_ready: 'window_not_ready',
    media_emulator_not_ready: 'emulator_not_ready', media_preview_unavailable: 'unavailable' };
  if (states[code]) assert.equal((await pending).state, states[code]);
  else await assert.rejects(pending, error => error instanceof clientModule.ControlFailure && error.code === code);
  assert.equal(closed, 1);
});

test('preview preserves Host capture time and releases a malformed image response', async () => {
  const deadlines = [];
  const session = load('control/frontend-session.ts', {}, deadlines);
  const clientModule = load('control/client.ts', { './frontend-session': session }, deadlines);
  const { InstalledTransport } = load('extensions/frontend-transport.ts', { '../control/client': clientModule }, deadlines);
  for (const malformed of [false, true]) {
    let closed = 0, released = 0;
    const capturedAt = '2026-10-30T01:02:03Z';
    const client = new clientModule.ControlClient('http://fixture', async url => {
      if (String(url).endsWith('/close')) { closed++; return new Response('{}'); }
      return new Response(JSON.stringify({ previewId: 'preview', runId: 'run', attemptId: 'attempt', source: 'pc_game' }));
    });
    const owner = new AbortController();
    const transport = new InstalledTransport('local', 'live-screenshot', 'activation', client, {
      acquire: async () => ({ blob: new Blob(), width: 640, height: 360, capturedAt: malformed ? undefined : capturedAt,
        release() { released++; } })
    }, owner.signal);
    const pending = transport.preview({ runId: 'run', attemptId: 'attempt' }, 'current-task', owner.signal);
    if (malformed) await assert.rejects(pending, /media_preview_response_invalid/);
    else { const result = await pending; assert.equal(result.state, 'ready'); assert.equal(result.capturedAt, capturedAt); result.image.release(); }
    assert.equal(released, 1); assert.equal(closed, 1);
  }
});

test('a late preview grant is closed after display cancellation without reading a frame', async () => {
  const deadlines = [];
  const session = load('control/frontend-session.ts', {}, deadlines);
  const clientModule = load('control/client.ts', { './frontend-session': session }, deadlines);
  const { InstalledTransport } = load('extensions/frontend-transport.ts', { '../control/client': clientModule }, deadlines);
  let deliver, closed = 0, frames = 0;
  const client = new clientModule.ControlClient('http://fixture', async url => {
    if (String(url).endsWith('/close')) { closed++; return new Response('{}'); }
    return new Promise(resolve => { deliver = () => resolve(new Response(JSON.stringify({ previewId: 'late', runId: 'run', attemptId: 'attempt' }))); });
  });
  const owner = new AbortController();
  const transport = new InstalledTransport('local', 'live-screenshot', 'activation', client,
    { acquire: async () => { frames++; throw Error('unexpected_frame'); } }, owner.signal);
  const pending = transport.preview({ runId: 'run', attemptId: 'attempt' }, 'current-task', owner.signal);
  owner.abort(); deliver(); await assert.rejects(pending, { name: 'AbortError' });
  assert.equal(closed, 1); assert.equal(frames, 0);
});
