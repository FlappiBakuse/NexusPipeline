import assert from 'node:assert/strict';
import { test } from 'node:test';
import { pathToFileURL } from 'node:url';

if (!process.env.NEXUS_SDK_ENTRY) throw new Error('NEXUS_SDK_ENTRY must identify the actual built SDK');
const { ExtensionScope } = await import(pathToFileURL(process.env.NEXUS_SDK_ENTRY).href);

test('cancels before reverse cleanup and all disposal callers wait for cleanup', async () => {
  const scope = new ExtensionScope();
  const events = [];
  scope.signal.addEventListener('abort', () => events.push('cancel'));
  await scope.own(() => events.push('first'));
  let release;
  const held = new Promise(resolve => { release = resolve; });
  await scope.own(async () => { events.push('second'); await held; });
  let firstDone = false, secondDone = false;
  const first = scope.dispose().then(() => { firstDone = true; });
  const second = scope.dispose().then(() => { secondDone = true; });
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(firstDone, false);
  assert.equal(secondDone, false);
  assert.deepEqual(events, ['cancel', 'second']);
  release();
  await Promise.all([first, second]);
  assert.deepEqual(events, ['cancel', 'second', 'first']);
});

test('disposes late contributions without resurrecting an aborted scope', async () => {
  const scope = new ExtensionScope();
  await scope.dispose();
  let disposed = false;
  await scope.own(() => { disposed = true; });
  assert.equal(disposed, true);
  assert.equal(scope.signal.aborted, true);
});

test('one failing cleanup does not leak other resources', async () => {
  const scope = new ExtensionScope();
  const events = [];
  await scope.own(() => events.push('retired'));
  await scope.own(() => { throw new Error('controlled cleanup failure'); });
  await assert.rejects(scope.dispose(), AggregateError);
  assert.deepEqual(events, ['retired']);
});

test('a contribution arriving during disposal is cleaned only after cancellation', async () => {
  const scope = new ExtensionScope();
  const completion = scope.dispose();
  let wasAbortedDuringCleanup = false;
  await scope.own(() => { wasAbortedDuringCleanup = scope.signal.aborted; });
  await completion;
  assert.equal(wasAbortedDuringCleanup, true);
});

test('disposing a child detaches it and does not dispose its siblings', async () => {
  const parent = new ExtensionScope();
  const child = await parent.child();
  const sibling = await parent.child();
  let childCleanups = 0;
  await child.own(() => { childCleanups++; });
  await child.dispose();
  assert.equal(parent.signal.aborted, false);
  assert.equal(sibling.signal.aborted, false);
  await parent.dispose();
  assert.equal(childCleanups, 1);
  assert.equal(sibling.signal.aborted, true);
});

test('a child requested after disposal is already aborted', async () => {
  const parent = new ExtensionScope();
  await parent.dispose();
  const child = await parent.child();
  assert.equal(child.signal.aborted, true);
  let released = false;
  await child.own(() => { released = true; });
  assert.equal(released, true);
});
