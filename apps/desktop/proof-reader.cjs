'use strict';
const crypto = require('node:crypto');
const { strictJson } = require('./strict-json.cjs');
const decoder = new TextDecoder('utf-8', { fatal: true });
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

function exactFields(value, names) {
  return value && typeof value === 'object' && !Array.isArray(value) &&
    Object.keys(value).length === names.length && names.every(name => Object.hasOwn(value, name));
}
function base64(value, maximum) {
  if (typeof value !== 'string' || value.length > maximum || !/^[A-Za-z0-9+/]+={0,2}$/.test(value)) throw new Error('desktop_proof_encoding');
  const bytes = Buffer.from(value, 'base64');
  if (bytes.toString('base64') !== value) throw new Error('desktop_proof_encoding');
  return bytes;
}
function verifiedPayload(text, trustedKey) {
  const envelope = strictJson(text);
  if (!exactFields(envelope, ['format', 'version', 'algorithm', 'payloadBase64', 'signatureBase64']) ||
      envelope.format !== 'nexus.desktop.proof' || envelope.version !== 1 || envelope.algorithm !== 'ES256')
    throw new Error('desktop_proof_format');
  const publicKey = base64(trustedKey, 256);
  const key = crypto.createPublicKey({ key: publicKey, format: 'der', type: 'spki' });
  const payload = base64(envelope.payloadBase64, 16384);
  const signature = base64(envelope.signatureBase64, 88);
  if (!key.export({format: 'der', type: 'spki'}).equals(publicKey) || signature.length !== 64 ||
      key.asymmetricKeyType !== 'ec' || key.asymmetricKeyDetails?.namedCurve !== 'prime256v1' ||
      !crypto.verify('sha256', payload, { key, dsaEncoding: 'ieee-p1363' }, signature)) throw new Error('desktop_proof_invalid');
  return strictJson(decoder.decode(payload));
}
function readProof(text, trustedKey, expectedFormat) {
  const value = verifiedPayload(text, trustedKey);
  const common = ['format', 'version', 'instanceId', 'clientId', 'sessionId'];
  const names = expectedFormat === 'nexus.desktop.session' ? [...common, 'correlationId', 'capabilities'] : [...common, 'correlationId', 'type', 'isApproved'];
  if (!exactFields(value, names) || value.format !== expectedFormat || value.version !== 1 ||
      ['instanceId', 'clientId', 'sessionId', 'correlationId'].some(name => !uuid.test(value[name]))) throw new Error('desktop_payload_invalid');
  if (expectedFormat === 'nexus.desktop.session') {
    if (!Array.isArray(value.capabilities) || new Set(value.capabilities).size !== value.capabilities.length ||
        value.capabilities.some(item => !['file.select', 'folder.select', 'external.open', 'close.request'].includes(item))) throw new Error('desktop_capabilities_invalid');
  } else if (expectedFormat !== 'nexus.desktop.channel' || value.type !== 'reply' || value.isApproved !== true || !uuid.test(value.correlationId))
    throw new Error('desktop_reply_invalid');
  return value;
}
async function responseText(response, allowConflict = false) {
  if (!response.ok && !(allowConflict && response.status === 409)) { await response.body?.cancel(); throw new Error('desktop_response_unconfirmed'); }
  const chunks = [];
  let length = 0;
  for await (const chunk of response.body) {
    length += chunk.length;
    if (length > 32768) throw new Error('desktop_response_limit');
    chunks.push(chunk);
  }
  return decoder.decode(Buffer.concat(chunks));
}
async function readClosePending(response) {
  if (response.status !== 409) return false;
  const value = strictJson(await responseText(response, true));
  return exactFields(value, ['format', 'version', 'code', 'message', 'correlationId']) &&
    value.format === 'nexus.control.error' && value.version === 1 && value.code === 'accepted_writes_pending' &&
    typeof value.message === 'string' && value.message.length <= 512 && uuid.test(value.correlationId);
}
function readFrontend(text, trustedKey, request, clientId) {
  const value = verifiedPayload(text, trustedKey);
  if (!exactFields(value, ['format', 'version', 'instanceId', 'nativeClientId', 'sessionId', 'correlationId', 'frontendClientId']) ||
      value.format !== 'nexus.desktop.frontend' || value.version !== 1 || value.frontendClientId !== clientId ||
      ['instanceId', 'nativeClientId', 'sessionId', 'correlationId'].some(key => value[key] !== request[key]) ||
      !uuid.test(value.frontendClientId)) throw new Error('desktop_frontend_binding_invalid');
  return value;
}
function readLogin(text, trustedKey, request) {
  const value = verifiedPayload(text, trustedKey);
  const common = ['format', 'version', 'instanceId', 'nativeClientId', 'sessionId', 'correlationId', 'type'];
  if (!exactFields(value, [...common, ...(request.type === 'poll' ? ['offers'] : ['captureId', 'nonce', ...(request.type === 'candidate' ? ['accepted'] : [])])]) ||
      value.format !== 'nexus.desktop.login' || value.version !== 1 ||
      ['instanceId', 'nativeClientId', 'sessionId', 'correlationId'].some(key => value[key] !== request[key])) throw new Error('desktop_login_reply_invalid');
  if (request.type === 'candidate') {
    if (value.type !== 'candidateResult' || value.captureId !== request.captureId || value.nonce !== request.nonce ||
        typeof value.accepted !== 'boolean') throw new Error('desktop_login_reply_invalid');
  } else if (request.type === 'complete') {
    if (value.type !== 'completed' || value.captureId !== request.captureId || value.nonce !== request.nonce) throw new Error('desktop_login_reply_invalid');
  } else if (request.type === 'poll') {
    if (value.type !== 'offers' || !Array.isArray(value.offers) || value.offers.length > 1) throw new Error('desktop_login_reply_invalid');
    for (const offer of value.offers) {
      if (!exactFields(offer, ['captureId', 'nonce', 'clientId', 'extensionId', 'activationId', 'operationId', 'policy', 'isCanceled']) ||
          ['captureId', 'nonce', 'clientId', 'activationId', 'operationId'].some(key => !uuid.test(offer[key])) ||
          typeof offer.extensionId !== 'string' || !/^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(offer.extensionId) || typeof offer.isCanceled !== 'boolean' ||
          !exactFields(offer.policy, ['id', 'startUrl', 'origins', 'popupOrigins', 'cookies', 'storage', 'jsonRequests', 'readySets', 'checks', 'equalSets']) ||
          JSON.stringify(offer.policy).length > 8192) throw new Error('desktop_login_offer_invalid');
    }
  } else throw new Error('desktop_login_reply_invalid');
  return value;
}
function readSession(text, trustedKey, expectedInstance, challenge) {
  const value = readProof(text, trustedKey, 'nexus.desktop.session');
  if (value.instanceId !== expectedInstance || value.correlationId !== challenge) throw new Error('desktop_host_instance_or_challenge_mismatch');
  return value;
}
function readClosePermit(text, trustedKey, request, now = Date.now()) {
  const value = verifiedPayload(text, trustedKey);
  const names = ['format', 'version', 'instanceId', 'clientId', 'sessionId', 'correlationId', 'type', 'isApproved', 'permitId', 'expiresAt'];
  if (!exactFields(value, names) || value.format !== 'nexus.desktop.channel' || value.version !== 1 ||
      value.type !== 'close.permit' || value.isApproved !== true || !uuid.test(value.permitId) ||
      ['instanceId', 'clientId', 'sessionId', 'correlationId'].some(key => value[key] !== request[key])) throw new Error('desktop_close_binding_invalid');
  const expiry = typeof value.expiresAt === 'string' ? Date.parse(value.expiresAt) : NaN;
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$/.test(value.expiresAt) ||
      !Number.isFinite(expiry) || expiry <= now || expiry > now + 31000) throw new Error('desktop_close_expiry_invalid');
  return value;
}
function readMode(text, trustedKey, request) {
  const value = verifiedPayload(text, trustedKey);
  if (!exactFields(value, ['format', 'version', 'instanceId', 'clientId', 'sessionId', 'correlationId', 'mode']) ||
      value.format !== 'nexus.desktop.mode' || value.version !== 1 ||
      ['instanceId', 'clientId', 'sessionId', 'correlationId', 'mode'].some(key => value[key] !== request[key]))
    throw new Error('desktop_mode_reply_invalid');
  return value;
}
module.exports = { readProof, readSession, readClosePermit, readClosePending, readFrontend, readLogin, readMode, responseText };
