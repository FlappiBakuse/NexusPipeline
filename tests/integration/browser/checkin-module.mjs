import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { qualifyCheckinRecovery } from './checkin-publication.mjs';

const input = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const token = process.env[input.tokenEnvironment];
const localOnly = input.context?.localOnly === true;
if (!token || !path.isAbsolute(input.output) || !path.isAbsolute(input.modules)) throw Error('browser_inputs_required');
const { chromium } = await import(pathToFileURL(path.join(input.modules, 'playwright-core/index.mjs')).href);
const executable = input.browser;
if (!path.isAbsolute(executable || '') || !fs.statSync(executable).isFile()) throw new Error('explicit_browser_required');
const hash = file => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
const sources = { script: hash(process.argv[1]), browser: hash(executable) };
const checks = [], errors = [], operations = [];
let browser, page, base, clientToken;
async function request(route, body, authenticated = true) {
  return fetch(new URL(route, input.address), { method: body === undefined ? 'GET' : 'POST', redirect: 'error',
    headers: { ...(authenticated ? { Authorization: 'Bearer ' + token } : {}),
      ...(authenticated && clientToken && route.includes('/invoke/') ? { 'X-Nexus-Frontend-Client': clientToken } : {}),
      ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }), signal: AbortSignal.timeout(15000) });
}
async function json(route, body) {
  const response = await request(route, body);
  assert.equal(response.ok, true, route + ':' + response.status);
  return response.json();
}
async function state() {
  const reply = await json(base + '/invoke/read-state', { input: {} });
  assert.equal(reply.ok, true, reply.error?.code ?? 'checkin_read_failed');
  return reply.value;
}
async function setEnabled(enabled) {
  const settings = await json('/control/v1/settings');
  settings.saved.values.extensions = settings.saved.values.extensions.map(item => ({ ...item,
    enabled: item.extensionId === 'game-checkin' ? enabled : item.enabled }));
  await json('/control/v1/settings', { format: 'nexus.control.host-settings', version: 1,
    operationId: crypto.randomUUID(), expectedRevision: settings.saved.revision, values: settings.saved.values });
  const installed = (await json('/control/v1/extensions')).items.find(item => item.extensionId === 'game-checkin');
  const current = await json('/control/v1/settings');
  await json('/control/v1/extensions/reconcile', { extensionId: 'game-checkin',
    expectedInstalledRevision: installed.installedRevision, expectedSettingsRevision: current.saved.revision });
}
try {
  const catalog = await json('/control/v1/extensions/frontend');
  const module = catalog.extensions.find(item => item.manifest.extensionId === 'game-checkin');
  assert.ok(module);
  base = `/control/v1/extensions/game-checkin/frontend/${module.activationId}`;
  if (!localOnly) {
    assert.ok([401, 403].includes((await request(base + '/assets/entry', undefined, false)).status));
    assert.ok([401, 403].includes((await request(base + '/invoke/read-state', { input: {} }, false)).status));
    checks.push('anonymous module and task state reads are refused');
  }
  const client = await fetch(new URL('/control/v1/extensions/clients', input.address), {
    method: 'POST', headers: { Authorization: 'Bearer ' + token }, signal: AbortSignal.timeout(15000) });
  assert.equal(client.ok, true);
  clientToken = (await client.json()).token;
  browser = await chromium.launch({ executablePath: executable, headless: true });
  page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
  page.setDefaultTimeout(15000);
  page.on('pageerror', error => errors.push(String(error)));
  page.on('request', event => {
    if (event.url().includes('/invoke/')) operations.push(new URL(event.url()).pathname.split('/').at(-1));
  });
  await page.goto(input.address);
  assert.equal(await page.evaluate(() => isSecureContext), localOnly);
  if (!localOnly) {
    await page.locator('.authentication input[type=password]').fill(token);
    await page.locator('.authentication button').click();
  }
  await page.locator('a[href$="/plugin/game-checkin/tasks"]').first().click();
  await page.getByRole('button', { name: '添加签到任务', exact: true }).waitFor();
  checks.push(localOnly ? 'installed production module opens on loopback' : 'installed production module opens on authenticated LAN HTTP');
  const initial = await state();
  assert.equal(initial.tasks.length, 0);
  assert.equal(initial.platforms.length, 6);
  assert.equal(initial.platforms.reduce((count, platform) => count + platform.games.length, 0), 14);
  checks.push('production state exposes six platforms and fourteen game choices');
  await page.getByRole('button', { name: '添加签到任务', exact: true }).click();
  const editor = page.locator('.checkin-editor');
  await editor.getByRole('textbox', { name: '任务名称', exact: true }).fill('Cancelled draft');
  await editor.getByRole('button', { name: '取消', exact: true }).click();
  await editor.waitFor({ state: 'detached' });
  assert.equal((await state()).tasks.length, 0);
  checks.push('cancelled editor leaves durable task state empty');
  await page.getByRole('button', { name: '添加签到任务', exact: true }).click();
  await editor.getByRole('textbox', { name: '任务名称', exact: true }).fill('Browser qualification');
  await editor.getByRole('switch', { name: '启用此任务', exact: true }).click();
  const platform = initial.platforms.find(item => item.id === 'cn');
  const selection = editor.locator(`nexus-select[aria-label=${JSON.stringify(platform.name + ' 签到游戏')}]`);
  await selection.locator('input[role=combobox]').click();
  await selection.locator(`[role=option][data-id=${JSON.stringify(platform.games[0].id)}]`).click();
  await editor.getByRole('textbox', { name: '任务名称', exact: true }).click();
  await editor.getByRole('button', { name: '保存', exact: true }).click();
  await editor.waitFor({ state: 'detached' });
  const saved = (await state()).tasks;
  assert.equal(saved.length, 1);
  assert.equal(saved[0].enabled, false);
  assert.deepEqual(saved[0].games.cn, [platform.games[0].id]);
  assert.deepEqual(saved[0].schedules, []);
  checks.push('actual save persists selected game with scheduling disabled');
  await page.locator('.checkin-task').getByRole('button', { name: '编辑签到', exact: true }).click();
  await editor.getByRole('textbox', { name: '任务名称', exact: true }).waitFor();
  assert.equal(await editor.getByRole('textbox', { name: '任务名称', exact: true }).inputValue(), 'Browser qualification');
  assert.equal(operations.includes('reveal-credential'), false);
  assert.equal(operations.includes('begin-browser-login'), false);
  checks.push('reopening saved task does not implicitly reveal credentials or start login');
  await editor.getByRole('textbox', { name: '任务名称', exact: true }).fill('Discarded change');
  await editor.getByRole('button', { name: '取消', exact: true }).click();
  await editor.waitFor({ state: 'detached' });
  assert.equal((await state()).tasks[0].name, 'Browser qualification');
  checks.push('cancelled edit preserves the previously saved task');
  const recovery = await qualifyCheckinRecovery({ page, state, checks });
  await setEnabled(false);
  await page.locator('.checkin-page').waitFor({ state: 'detached' });
  assert.equal((await request(base + '/assets/entry')).ok, false);
  checks.push('disable removes the task surface and revokes the original activation');
  await setEnabled(true);
  const renewed = (await json('/control/v1/extensions/frontend')).extensions.find(item => item.manifest.extensionId === 'game-checkin');
  assert.notEqual(renewed.activationId, module.activationId);
  base = `/control/v1/extensions/game-checkin/frontend/${renewed.activationId}`;
  assert.equal((await state()).tasks[0].name, 'Browser qualification');
  assert.equal(operations.includes('run-task'), false);
  assert.deepEqual(errors, []);
  checks.push('reactivation retains the saved task without executing sign-in');
  assert.deepEqual({ script: hash(process.argv[1]), browser: hash(executable) }, sources);
  fs.writeFileSync(path.join(input.output, 'result.json'), JSON.stringify({ status: 'PASS', checks, sources, recovery,
    qualification: 'Original installed CheckIn package and production browser UI; isolated disabled task, no account or sign-in execution', network: localOnly ? 'loopback; LAN authorization excluded' : 'authenticated LAN' }, null, 2));
} catch (error) {
  fs.writeFileSync(path.join(input.output, 'result.json'), JSON.stringify({ status: 'FAIL', checks, errors,
    failure: String(error.stack || error).replaceAll(token, '[redacted]'), sources,
    visibleText: page ? (await page.locator('body').innerText().catch(() => '')).slice(-4000) : '' }, null, 2));
  process.exitCode = 1;
} finally { await browser?.close(); }
