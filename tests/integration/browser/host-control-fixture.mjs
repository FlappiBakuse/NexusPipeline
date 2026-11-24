import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import fs from 'node:fs';

export function hostControlFixtureSha256() {
  return crypto.createHash('sha256').update(fs.readFileSync(new URL(import.meta.url))).digest('hex');
}

export function hostControlFixture(address, token, extensionId) {
  async function request(route, body, authenticated = true) {
    return fetch(new URL(route, address), { method: body === undefined ? 'GET' : 'POST', redirect: 'error',
      headers: { ...(authenticated ? { Authorization: 'Bearer ' + token } : {}),
        ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) },
      ...(body === undefined ? {} : { body: JSON.stringify(body) }), signal: AbortSignal.timeout(15000) });
  }

  async function json(route, body) {
    const response = await request(route, body);
    assert.equal(response.ok, true, route + ':' + response.status);
    return response.json();
  }

  async function enable(enabled) {
    const settings = await json('/control/v1/settings');
    settings.saved.values.extensions = settings.saved.values.extensions.map(item => ({ ...item,
      enabled: item.extensionId === extensionId ? enabled : item.enabled }));
    await json('/control/v1/settings', { format: 'nexus.control.host-settings', version: 1,
      operationId: crypto.randomUUID(), expectedRevision: settings.saved.revision, values: settings.saved.values });
    const installed = (await json('/control/v1/extensions')).items.find(item => item.extensionId === extensionId);
    const current = await json('/control/v1/settings');
    await json('/control/v1/extensions/reconcile', { extensionId,
      expectedInstalledRevision: installed.installedRevision, expectedSettingsRevision: current.saved.revision });
  }

  return { request, json, enable };
}
