'use strict';
const { ipcMain } = require('electron');
const crypto = require('node:crypto');
const { readFrontend, responseText } = require('./proof-reader.cjs');
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

function installFrontendBridge(config, validateSender, connect, generation) {
  const clients = new Map();
  const address = new URL(config.address);
  const identity = session => ({ format: 'nexus.desktop.frontend', version: 1, instanceId: session.instanceId,
    nativeClientId: session.clientId, sessionId: session.sessionId, correlationId: crypto.randomUUID() });
  async function post(path, request, clientId, token) {
    const headers = { 'Content-Type': 'application/json' };
    if (token) headers['X-Nexus-Frontend-Client'] = token;
    const response = await fetch(new URL(path, address), { method: 'POST', headers, body: JSON.stringify(request),
      redirect: 'error', signal: AbortSignal.timeout(5000) });
    return readFrontend(await responseText(response), config.verificationKey, request, clientId);
  }
  async function retire(clientId, session) {
    const request = { ...identity(session), frontendClientId: clientId };
    await post('control/v1/desktop/frontend/retire', request, clientId);
  }
  ipcMain.handle('desktop.frontend.bind', async (event, input) => {
    validateSender(event);
    if (!input || Object.keys(input).length !== 3 || Object.keys(input).some(key => !['clientId', 'token', 'sessionId'].includes(key)) ||
        !uuid.test(input.clientId) || typeof input.token !== 'string' || !/^[a-f0-9]{64}$/.test(input.token))
      throw new Error('desktop_frontend_request_invalid');
    const current = generation(); const session = await connect(); validateSender(event);
    if (input.sessionId !== session.sessionId || current !== generation()) throw new Error('desktop_document_changed');
    if (clients.size >= 128 && !clients.has(input.clientId)) throw new Error('desktop_frontend_limit');
    const request = { ...identity(session), frontendClientId: input.clientId };
    try {
      await post('control/v1/desktop/frontend/bind', request, input.clientId, input.token);
      if (current !== generation()) throw new Error('desktop_document_changed');
      validateSender(event); clients.set(input.clientId, session); return { clientId: input.clientId };
    } catch (error) {
      await retire(input.clientId, session).catch(() => {}); throw error;
    }
  });
  ipcMain.handle('desktop.frontend.retire', async (event, input) => {
    validateSender(event);
    if (!input || Object.keys(input).length !== 1 || !uuid.test(input.clientId)) throw new Error('desktop_frontend_request_invalid');
    const session = clients.get(input.clientId); if (!session) return;
    clients.delete(input.clientId); await retire(input.clientId, session); validateSender(event);
  });
  function invalidate() {
    for (const [id, session] of clients) void retire(id, session).catch(() => {});
    clients.clear();
  }
  return Object.freeze({ invalidate, dispose: () => {
    invalidate(); ipcMain.removeHandler('desktop.frontend.bind'); ipcMain.removeHandler('desktop.frontend.retire');
  } });
}
module.exports = { installFrontendBridge };
