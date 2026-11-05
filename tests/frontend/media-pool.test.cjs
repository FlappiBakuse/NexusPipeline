'use strict';
const assert = require('node:assert/strict');
const test = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const ts = require(process.env.NEXUS_FRONTEND_TYPESCRIPT);
const root = process.env.NEXUS_FRONTEND_SOURCE || path.join(__dirname, '../../apps/web/src/control');
function load(name, dependencies = {}, extra = {}) {
  const source = fs.readFileSync(path.join(root, name + '.ts'), 'utf8');
  const code = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } });
  const sandbox = { exports: {}, Blob, URL, Headers, Response, TextDecoder, Uint8Array, DataView,
    AbortController, AbortSignal, ...extra,
    require(key) { if (!(key in dependencies)) throw Error('unexpected_dependency:' + key); return dependencies[key]; } };
  vm.runInNewContext(code.outputText, sandbox); return sandbox.exports;
}
function fixture(send) {
  const urls = new Set(); let serial = 0;
  const client = load('client', { './frontend-session': {} });
  const format = load('image-format');
  const { MediaPool } = load('media-pool', { './client': client, './image-format': format }, {
    URL: { createObjectURL() { const value = 'blob:' + serial++; urls.add(value); return value; },
      revokeObjectURL(value) { assert.equal(urls.delete(value), true); } }
  });
  return { pool: new MediaPool({ response: send }), urls };
}
function png(size = 33) {
  const bytes = Buffer.alloc(size);
  bytes.writeUInt32BE(0x89504e47); bytes.writeUInt32BE(0x0d0a1a0a, 4);
  bytes.writeUInt32BE(13, 8); bytes.writeUInt32BE(0x49484452, 12);
  bytes.writeUInt32BE(480, 16); bytes.writeUInt32BE(360, 20); return bytes;
}
function response(bytes, length = bytes.length) {
  return new Response(bytes, { headers: { 'Content-Type': 'image/png', 'Content-Length': String(length) } });
}
async function until(check) {
  const deadline = Date.now() + 5000;
  while (!check()) { assert.ok(Date.now() < deadline, 'fixture_condition_timeout'); await new Promise(resolve => setImmediate(resolve)); }
}
function empty(f) {
  assert.equal(f.pool.metrics.encodedBytes, 0); assert.equal(f.pool.metrics.downloadBytes, 0);
  assert.equal(f.pool.metrics.active, 0); assert.equal(f.pool.metrics.queued, 0); assert.equal(f.urls.size, 0);
}
test('shared downloads retain one Blob without registering an object URL', async () => {
  let calls = 0;
  const f = fixture(async () => { calls++; return response(png()); });
  const a = new AbortController(), b = new AbortController();
  const [first, second] = await Promise.all([f.pool.acquire('/same', a.signal), f.pool.acquire('/same', b.signal)]);
  assert.equal(calls, 1); assert.equal(first.blob, second.blob); assert.equal(f.pool.metrics.encodedBytes, 33); assert.equal(f.urls.size, 0);
  a.abort(); assert.equal(f.pool.metrics.encodedBytes, 33); assert.equal(f.urls.size, 0); second.release(); first.release(); empty(f);
});
test('encoded budget waits for actual lease release and canceled waiters retain no buffers or URLs', async () => {
  const size = 20 * 1024 * 1024;
  const f = fixture(async () => response(png(size)));
  const first = await f.pool.acquire('/first', new AbortController().signal);
  const owner = new AbortController();
  const blocked = f.pool.acquire('/second', owner.signal);
  const rejected = assert.rejects(blocked, { name: 'AbortError' });
  await until(() => f.pool.metrics.downloadBytes === size);
  assert.equal(f.urls.size, 0); owner.abort(); await rejected;
  await until(() => f.pool.metrics.active === 0);
  assert.equal(f.pool.metrics.encodedBytes, size); assert.equal(f.pool.metrics.downloadBytes, 0);
  const next = f.pool.acquire('/third', new AbortController().signal);
  await until(() => f.pool.metrics.downloadBytes === size);
  first.release(); const lease = await next; assert.equal(f.urls.size, 0); lease.release(); empty(f);
});
test('download reservations limit simultaneous response consumption and release after abort', async () => {
  const readers = []; const size = 20 * 1024 * 1024;
  const f = fixture(async (_path, signal) => {
    const body = new ReadableStream({ start(controller) {
      const abort = () => controller.error(signal.reason);
      signal.addEventListener('abort', abort, { once: true });
      readers.push({ finish() { signal.removeEventListener('abort', abort); controller.enqueue(png(size)); controller.close(); } });
    } });
    return new Response(body, { headers: { 'Content-Type': 'image/png', 'Content-Length': String(size) } });
  });
  const a = new AbortController(), b = new AbortController();
  const first = f.pool.acquire('/first', a.signal), second = f.pool.acquire('/second', b.signal);
  const rejected = assert.rejects(first, { name: 'AbortError' });
  await until(() => readers.length === 2 && f.pool.metrics.downloadBytes === size);
  assert.equal(f.pool.metrics.active, 2); a.abort(); await rejected;
  readers[1].finish(); const lease = await second; lease.release();
  await until(() => f.pool.metrics.active === 0); empty(f);
});
test('invalid declared lengths and truncated bodies fail without retaining capacity', async () => {
  for (const length of [0, -1, 33.5, 32 * 1024 * 1024 + 1, 34]) {
    const f = fixture(async () => response(png(), length));
    await assert.rejects(f.pool.acquire('/invalid', new AbortController().signal), /image_download_length_invalid/);
    empty(f);
  }
});
