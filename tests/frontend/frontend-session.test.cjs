'use strict';
const assert = require('node:assert/strict');
const test = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
if (!process.env.NEXUS_FRONTEND_TYPESCRIPT) throw new Error('Pass the verified TypeScript module path');
const typescript = require(process.env.NEXUS_FRONTEND_TYPESCRIPT);
const source = fs.readFileSync(path.join(__dirname, '../../apps/web/src/control/frontend-session.ts'), 'utf8');
const script = typescript.transpileModule(source, { compilerOptions: { module: typescript.ModuleKind.CommonJS } });
const sandbox = { exports: {}, Uint8Array, TextDecoder, Headers, URL, AbortController, AbortSignal };
vm.runInNewContext(script.outputText, sandbox);
const { FrontendSession } = sandbox.exports;
const route = '/control/v1/extensions/fixture/frontend/activation/invoke/read-state';
const receipt = (character, clientId = '70000000-0000-4000-8000-000000000001') => new Response(JSON.stringify({ clientId,
  token: character.repeat(64), expiresAt: new Date(Date.now() + 60000).toISOString() }));
const authorization = () => new Headers({ Authorization: 'Bearer synthetic' });

test('concurrent business calls share one host grant and ordinary routes do not mint a client', async () => {
  let calls = 0, resolve;
  const session = new FrontendSession('http://fixture', async () => { calls++; return new Promise(value => { resolve = value; }); }, authorization);
  const ordinary = await session.headers('/control/v1/settings');
  assert.equal(ordinary.has('X-Nexus-Frontend-Client'), false); assert.equal(calls, 0);
  const first = session.headers(route), second = session.headers(route);
  assert.equal(calls, 1); resolve(receipt('a'));
  assert.equal((await first).get('X-Nexus-Frontend-Client'), 'a'.repeat(64));
  assert.equal((await second).get('X-Nexus-Frontend-Client'), 'a'.repeat(64));
  assert.equal((await session.headers(route)).get('X-Nexus-Frontend-Client'), 'a'.repeat(64)); assert.equal(calls, 1);
});

test('independent browser owners do not share client grants even with identical authorization', async () => {
  let calls = 0;
  const send = async () => receipt(calls++ === 0 ? 'a' : 'b');
  const first = new FrontendSession('http://fixture', send, authorization);
  const second = new FrontendSession('http://fixture', send, authorization);
  assert.notEqual((await first.headers(route)).get('X-Nexus-Frontend-Client'), (await second.headers(route)).get('X-Nexus-Frontend-Client'));
});

test('a late grant from an old authentication generation cannot replace the new browser owner', async () => {
  const held = [];
  const session = new FrontendSession('http://fixture', async url => String(url).endsWith('/retire') ?
    new Response(null, { status: 204 }) : new Promise(resolve => held.push(resolve)), authorization);
  const old = session.headers(route); const rejected = assert.rejects(old, /frontend_client_changed/);
  session.reset();
  const current = session.headers(route);
  held[1](receipt('b')); assert.equal((await current).get('X-Nexus-Frontend-Client'), 'b'.repeat(64));
  held[0](receipt('a')); await rejected;
  assert.equal((await session.headers(route)).get('X-Nexus-Frontend-Client'), 'b'.repeat(64));
});

test('native binding must finish for the exact parsed host client before a business header is returned', async () => {
  const bound = []; let release;
  const native = { bindFrontend: (id, token) => { bound.push({ id, token }); return new Promise(resolve => { release = resolve; }); },
    retireFrontend: async () => {} };
  const session = new FrontendSession('http://fixture', async () => receipt('a'), authorization, native);
  const pending = session.headers(route); let completed = false; void pending.then(() => { completed = true; });
  while (!release) await new Promise(resolve => setImmediate(resolve));
  assert.equal(completed, false); assert.equal(bound[0].id, '70000000-0000-4000-8000-000000000001');
  assert.equal(bound[0].token, 'a'.repeat(64)); release();
  assert.equal((await pending).get('X-Nexus-Frontend-Client'), 'a'.repeat(64));
});

test('a late native bind is retired under original authorization after an authentication generation changes', async () => {
  let release; const nativeRetired = [], requests = [];
  const native = { bindFrontend: () => new Promise(resolve => { release = resolve; }), retireFrontend: async id => { nativeRetired.push(id); } };
  let auth = 'original';
  const session = new FrontendSession('http://fixture', async (url, init) => {
    requests.push({ url: String(url), authorization: init.headers.get('Authorization'),
      token: init.headers.get('X-Nexus-Frontend-Client') });
    return String(url).endsWith('/retire') ? new Response(null, { status: 204 }) : receipt('a');
  }, () => new Headers({ Authorization: 'Bearer ' + auth }), native);
  const pending = session.headers(route); const rejected = assert.rejects(pending, /frontend_client_changed/);
  while (!release) await new Promise(resolve => setImmediate(resolve));
  session.reset(); auth = 'replacement'; release(); await rejected;
  assert.deepEqual(nativeRetired, ['70000000-0000-4000-8000-000000000001']);
  const retirements = requests.filter(request => request.url === 'http://fixture/control/v1/extensions/clients/retire');
  assert.ok(retirements.length > 0);
  for (const request of retirements) {
    assert.equal(request.authorization, 'Bearer original'); assert.equal(request.token, 'a'.repeat(64));
  }
});

test('a failed native bind cannot cache an unbound grant and retires the actual server grant', async () => {
  const grants = [
    { id: '70000000-0000-4000-8000-000000000001', character: 'a' },
    { id: '70000000-0000-4000-8000-000000000002', character: 'b' }
  ];
  const retired = [], serverRetired = [];
  let issued = 0;
  const native = { bindFrontend: async () => { throw new Error('native_binding_failed'); },
    retireFrontend: async id => { retired.push(id); } };
  const session = new FrontendSession('http://fixture', async (url, init) => {
    if (String(url).endsWith('/retire')) {
      serverRetired.push({ token: init.headers.get('X-Nexus-Frontend-Client'),
        authorization: init.headers.get('Authorization') });
      return new Response(null, { status: 204 });
    }
    const grant = grants[issued++];
    assert.ok(grant, 'unexpected grant allocation');
    return receipt(grant.character, grant.id);
  }, authorization, native);
  for (const grant of grants) {
    await assert.rejects(session.headers(route), /native_binding_failed/);
    assert.ok(retired.includes(grant.id), 'failed native binding releases its own client');
    const retirements = serverRetired.filter(request => request.token === grant.character.repeat(64));
    assert.ok(retirements.length > 0, 'failed binding retires its own server token');
    assert.ok(retirements.every(request => request.authorization === 'Bearer synthetic'));
  }

});

test('canceling one waiter leaves the shared host grant available to another waiter', async () => {
  let resolve;
  const session = new FrontendSession('http://fixture', async () => new Promise(value => { resolve = value; }), authorization);
  const cancel = new AbortController();
  const first = session.headers(route, cancel.signal); const rejected = assert.rejects(first, /caller_closed/);
  const second = session.headers(route); cancel.abort(new Error('caller_closed'));
  await rejected; resolve(receipt('a'));
  assert.equal((await second).get('X-Nexus-Frontend-Client'), 'a'.repeat(64));
});

test('authentication reset retires the old grant with its original authorization and keeps tokens out of URLs', async () => {
  const requests = []; let token = 'original';
  const session = new FrontendSession('http://fixture', async (url, init) => {
    requests.push({ url: String(url), headers: init.headers });
    return String(url).endsWith('/retire') ? new Response(null, { status: 204 }) : receipt('a');
  }, () => new Headers({ Authorization: 'Bearer ' + token }));
  await session.headers(route); session.reset(); token = 'replacement';
  const retirements = requests.filter(request => request.url === 'http://fixture/control/v1/extensions/clients/retire');
  assert.ok(retirements.length > 0);
  for (const request of retirements) {
    assert.equal(request.headers.get('Authorization'), 'Bearer original');
    assert.equal(request.headers.get('X-Nexus-Frontend-Client'), 'a'.repeat(64));
  }
  assert.ok(requests.every(request => !request.url.includes('a'.repeat(64)) && !request.url.includes('original')));

});

test('oversized and malformed grant responses cannot become a browser client identity', async () => {
  for (const response of [new Response('x'.repeat(1025)), new Response(JSON.stringify({ token: 'invented' }))]) {
    const session = new FrontendSession('http://fixture', async () => response, authorization);
    await assert.rejects(session.headers(route), /frontend_client_invalid/);
  }
});
