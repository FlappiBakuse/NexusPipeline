import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const input = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const token = process.env[input.tokenEnvironment];
if (!token || !path.isAbsolute(input.output) || !path.isAbsolute(input.modules)) throw new Error('browser_inputs_required');
const { chromium } = await import(pathToFileURL(path.join(input.modules, 'playwright-core/index.mjs')).href);
const executable = input.browser;
const local = ['127.0.0.1', 'localhost'].includes(new URL(input.address).hostname);
if (!path.isAbsolute(executable || '') || !fs.statSync(executable).isFile()) throw new Error('explicit_browser_required');
const hash = file => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
const before = { script: hash(process.argv[1]), browser: hash(executable) };
const checks = [], requests = [], errors = [];
let browser;
async function request(route, body, authenticated = true) {
  const response = await fetch(new URL(route, input.address), {
    method: body === undefined ? 'GET' : 'POST', redirect: 'error',
    headers: { ...(authenticated ? { Authorization: 'Bearer ' + token } : {}),
      ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }), signal: AbortSignal.timeout(15000)
  });
  return response;
}
async function json(route, body) {
  const response = await request(route, body);
  assert.equal(response.ok, true, route + ':' + response.status);
  return response.json();
}
async function setEnabled(enabled) {
  const settings = await json('/control/v1/settings');
  settings.saved.values.extensions = settings.saved.values.extensions.map(item => ({ ...item,
    enabled: item.extensionId === 'live-screenshot' ? enabled : item.enabled }));
  await json('/control/v1/settings', { format: 'nexus.control.host-settings', version: 1,
    operationId: crypto.randomUUID(), expectedRevision: settings.saved.revision, values: settings.saved.values });
  const state = await json('/control/v1/extensions');
  const installed = state.items.find(item => item.extensionId === 'live-screenshot');
  const current = await json('/control/v1/settings');
  await json('/control/v1/extensions/reconcile', { extensionId: 'live-screenshot',
    expectedInstalledRevision: installed.installedRevision, expectedSettingsRevision: current.saved.revision });
}
try {
  const anonymous = await request('/control/v1/extensions/frontend', undefined, false);
  assert.ok(local ? anonymous.ok : [401, 403].includes(anonymous.status));
  checks.push(local ? 'loopback catalog retains local-user access' : 'anonymous catalog refused');
  const catalog = await json('/control/v1/extensions/frontend');
  const module = catalog.extensions.find(item => item.manifest.extensionId === 'live-screenshot'); assert.ok(module);
  const base = `/control/v1/extensions/live-screenshot/frontend/${module.activationId}`;
  const anonymousAsset = await request(base + '/assets/entry', undefined, false);
  assert.ok(local ? anonymousAsset.ok : [401, 403].includes(anonymousAsset.status));
  checks.push(local ? 'loopback module asset retains local-user access' : 'anonymous module asset refused');
  const root = await request('/', undefined, false);
  const csp = root.headers.get('content-security-policy'); assert.ok(csp?.includes("script-src 'self' blob:"));
  checks.push('actual page CSP permits its authenticated blob module');
  browser = await chromium.launch({ executablePath: executable, headless: true });
  const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
  page.setDefaultTimeout(15000); page.on('pageerror', error => errors.push(String(error)));
  page.on('request', event => {
    if (event.url().includes('/frontend/') && event.url().includes('/assets/'))
      requests.push({ path: new URL(event.url()).pathname, authenticated: event.headers().authorization === 'Bearer ' + token });
  });
  await page.goto(input.address);
  const context = await page.evaluate(() => ({ secure: isSecureContext,
    randomUUID: typeof crypto.randomUUID, getRandomValues: typeof crypto.getRandomValues }));
  assert.equal(context.secure, local); assert.equal(context.getRandomValues, 'function');
  if (local) await page.locator('.authentication').waitFor({ state: 'detached' });
  else {
    await page.locator('.authentication input[type=password]').fill(token);
    await page.locator('.authentication button').click();
  }
  await page.waitForFunction(() => document.querySelectorAll('style[data-extension-id="live-screenshot"]').length === 1);
  assert.ok(requests.length >= 2 && requests.every(item => item.authenticated === !local));
  checks.push(local ? 'actual loopback app loads its module and stylesheet without retaining a remote bearer' :
    'actual app authenticated both module and stylesheet requests');
  assert.equal(errors.length, 0); checks.push('actual browser activated the packaged frontend factory');
  await page.screenshot({ path: path.join(input.output, 'active-module.png') });
  await setEnabled(false);
  await page.waitForFunction(() => !document.querySelector('style[data-extension-id="live-screenshot"]'));
  const stale = await request(base + '/assets/entry'); assert.ok(!stale.ok);
  checks.push('disable revokes original activation and removes its browser style');
  await setEnabled(true);
  await page.waitForFunction(() => document.querySelectorAll('style[data-extension-id="live-screenshot"]').length === 1);
  const renewed = await json('/control/v1/extensions/frontend');
  assert.notEqual(renewed.extensions.find(item => item.manifest.extensionId === 'live-screenshot').activationId, module.activationId);
  assert.equal(errors.length, 0); checks.push('reenable creates exactly one replacement browser owner');
  assert.deepEqual({ script: hash(process.argv[1]), browser: hash(executable) }, before);
  fs.writeFileSync(path.join(input.output, 'result.json'), JSON.stringify({ status: 'PASS',
    scope: 'Actual Test Host, original built plugin ZIP and production app frontend loader in isolated Edge; no frame/device or production trust claim',
    checks, requests, errors, source: before, context, browserVersion: browser.version(), csp }, null, 2));
} catch (error) {
  let extensions;
  try { extensions = await json('/control/v1/extensions'); } catch { extensions = 'query unavailable'; }
  fs.writeFileSync(path.join(input.output, 'result.json'), JSON.stringify({ status: 'FAIL', checks, requests,
    errors, extensions, failure: String(error).replaceAll(token, '[redacted]'), source: before }, null, 2));
  process.exitCode = 1;
} finally { await browser?.close(); }
