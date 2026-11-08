'use strict';
const assert = require('node:assert/strict');
const test = require('node:test');
const crypto = require('node:crypto');
const { readLogin } = require('../../apps/desktop/proof-reader.cjs');
const key = crypto.generateKeyPairSync('ec', { namedCurve: 'prime256v1' });
const trusted = key.publicKey.export({ type: 'spki', format: 'der' }).toString('base64');
const request = { format: 'nexus.desktop.login', version: 1, type: 'candidate',
  ...Object.fromEntries(['instanceId', 'nativeClientId', 'sessionId', 'correlationId', 'captureId', 'nonce']
    .map(name => [name, crypto.randomUUID()])) };
const reply = accepted => ({ ...request, type: 'candidateResult', accepted });
function signed(value) {
  const payload = Buffer.from(JSON.stringify(value));
  return JSON.stringify({ format: 'nexus.desktop.proof', version: 1, algorithm: 'ES256',
    payloadBase64: payload.toString('base64'), signatureBase64: crypto.sign('sha256', payload,
      { key: key.privateKey, dsaEncoding: 'ieee-p1363' }).toString('base64') });
}

test('signed candidate decisions preserve acceptance and rejection for the current capture', () => {
  for (const accepted of [true, false])
    assert.equal(readLogin(signed(reply(accepted)), trusted, request).accepted, accepted);
});

test('candidate replies reject another session, capture, nonce, or decision shape', () => {
  for (const name of ['instanceId', 'nativeClientId', 'sessionId', 'correlationId', 'captureId', 'nonce'])
    assert.throws(() => readLogin(signed({ ...reply(true), [name]: crypto.randomUUID() }), trusted, request));
  for (const accepted of [null, 'true', 1, undefined])
    assert.throws(() => readLogin(signed(reply(accepted)), trusted, request));
  for (const patch of [{ type: 'completed' }, { type: 'offers' }, { offers: [] }])
    assert.throws(() => readLogin(signed({ ...reply(true), ...patch }), trusted, request));
  const envelope = JSON.parse(signed(reply(false)));
  envelope.payloadBase64 = Buffer.from(JSON.stringify(reply(true))).toString('base64');
  assert.throws(() => readLogin(JSON.stringify(envelope), trusted, request), /desktop_proof_invalid/);
});

test('poll and completion remain separate signed login transitions', () => {
  const { captureId, nonce, ...poll } = { ...request, type: 'poll' };
  assert.deepEqual(readLogin(signed({ ...poll, type: 'offers', offers: [] }), trusted, poll).offers, []);
  const complete = { ...request, type: 'complete' };
  assert.equal(readLogin(signed({ ...complete, type: 'completed' }), trusted, complete).captureId, captureId);
  assert.throws(() => readLogin(signed(reply(true)), trusted, complete));
  assert.throws(() => readLogin(signed({ ...complete, type: 'completed', nonce: crypto.randomUUID() }), trusted, complete));
});
