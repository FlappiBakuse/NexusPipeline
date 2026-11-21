'use strict';
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const { test } = require('node:test');
const { readCloseRejection } = require('../../apps/desktop/proof-reader.cjs');

const { publicKey, privateKey } = crypto.generateKeyPairSync('ec', { namedCurve: 'prime256v1' });
const trust = publicKey.export({ type: 'spki', format: 'der' }).toString('base64');
const request = {
  format: 'nexus.desktop.channel', version: 1, instanceId: crypto.randomUUID(), clientId: crypto.randomUUID(),
  sessionId: crypto.randomUUID(), correlationId: crypto.randomUUID()
};
const rejection = { ...request, type: 'close.rejected', isApproved: false, code: 'recovery_pending' };

function sign(value) {
  const payload = Buffer.from(JSON.stringify(value));
  const signature = crypto.sign('sha256', payload, { key: privateKey, dsaEncoding: 'ieee-p1363' });
  return JSON.stringify({ format: 'nexus.desktop.proof', version: 1, algorithm: 'ES256',
    payloadBase64: payload.toString('base64'), signatureBase64: signature.toString('base64') });
}

test('ordinary quit can surface the signed reason for its own rejection', () => {
  assert.equal(readCloseRejection(sign(rejection), trust, request), 'recovery_pending');
});

test('ordinary quit rejects unsigned, mismatched and unknown rejection reasons', () => {
  assert.throws(() => readCloseRejection(JSON.stringify(rejection), trust, request), { message: 'desktop_proof_format' });
  assert.throws(() => readCloseRejection(sign({ ...rejection, correlationId: crypto.randomUUID() }), trust, request),
    { message: 'desktop_close_rejection_invalid' });
  assert.throws(() => readCloseRejection(sign({ ...rejection, code: 'unknown' }), trust, request),
    { message: 'desktop_close_rejection_invalid' });
});
