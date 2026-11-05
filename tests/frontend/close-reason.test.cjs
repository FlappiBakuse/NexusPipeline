'use strict';
const assert = require('node:assert/strict');
const test = require('node:test');
const crypto = require('node:crypto');
const { readClosePending } = require('../../apps/desktop/proof-reader.cjs');
const frame = code => ({ format: 'nexus.control.error', version: 1, code,
  message: 'The control request could not be completed.', correlationId: crypto.randomUUID() });

test('only the bounded current pending-work conflict permits another close attempt', async () => {
  assert.equal(await readClosePending(new Response(JSON.stringify(frame('accepted_writes_pending')), { status: 409 })), true);
  for (const code of ['close_session_mismatch', 'recovery_pending', 'access_denied'])
    assert.equal(await readClosePending(new Response(JSON.stringify(frame(code)), { status: 409 })), false);
  assert.equal(await readClosePending(new Response('{}', { status: 200 })), false);
  assert.equal(await readClosePending(new Response(JSON.stringify({ ...frame('accepted_writes_pending'), extra: true }), { status: 409 })), false);
});

test('ambiguous or oversized close conflicts cannot authorize retries', async () => {
  const valid = JSON.stringify(frame('accepted_writes_pending'));
  await assert.rejects(readClosePending(new Response(valid.replace('"code":', '"code":"access_denied","code":'), { status: 409 })));
  await assert.rejects(readClosePending(new Response(' '.repeat(32769), { status: 409 })), /desktop_response_limit/);
});
