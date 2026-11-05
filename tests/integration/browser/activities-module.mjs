import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { setTimeout } from 'node:timers/promises';

const input = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const token = process.env[input.tokenEnvironment];
const localOnly = input.context?.localOnly === true;
if (!token || !path.isAbsolute(input.output) || !path.isAbsolute(input.modules)) throw new Error('browser_inputs_required');
const { chromium } = await import(pathToFileURL(path.join(input.modules, 'playwright-core/index.mjs')).href);
const executable = input.browser;
if (!path.isAbsolute(executable || '') || !fs.statSync(executable).isFile()) throw new Error('explicit_browser_required');
const hash = file => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
const source = { script: hash(process.argv[1]), browser: hash(executable) };
const checks = [], requests = [], errors = [];
let browser, page, base;

async function request(route, body, authenticated = true) {
  return fetch(new URL(route, input.address), { method: body === undefined ? 'GET' : 'POST', redirect: 'error',
    headers: { ...(authenticated ? { Authorization: 'Bearer ' + token } : {}),
      ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }), signal: AbortSignal.timeout(15000) });
}
async function json(route, body) {
  const result = await request(route, body);
  assert.equal(result.ok, true, route + ':' + result.status);
  return result.json();
}
const state = () => json(base + '/invoke/read-state', { input: {} });
async function awaitState(predicate) {
  const deadline = Date.now() + 35000;
  while (Date.now() < deadline) {
    const value = await state();
    if (predicate(value)) return value;
    await setTimeout(100);
  }
  throw new Error('activities_state_timeout');
}
async function enable(enabled) {
  const settings = await json('/control/v1/settings');
  settings.saved.values.extensions = settings.saved.values.extensions.map(item => ({ ...item,
    enabled: item.extensionId === 'game-activities' ? enabled : item.enabled }));
  await json('/control/v1/settings', { format: 'nexus.control.host-settings', version: 1,
    operationId: crypto.randomUUID(), expectedRevision: settings.saved.revision, values: settings.saved.values });
  const installed = (await json('/control/v1/extensions')).items.find(item => item.extensionId === 'game-activities');
  const current = await json('/control/v1/settings');
  await json('/control/v1/extensions/reconcile', { extensionId: 'game-activities',
    expectedInstalledRevision: installed.installedRevision, expectedSettingsRevision: current.saved.revision });
}

try {
  const catalog = await json('/control/v1/extensions/frontend');
  const module = catalog.extensions.find(item => item.manifest.extensionId === 'game-activities');
  assert.ok(module);
  base = `/control/v1/extensions/game-activities/frontend/${module.activationId}`;
  if (!localOnly) {
    assert.ok([401, 403].includes((await request(base + '/assets/entry', undefined, false)).status));
    assert.ok([401, 403].includes((await request(base + '/invoke/read-state', { input: {} }, false)).status));
    checks.push('module and business reads require control authorization');
  }
  browser = await chromium.launch({ executablePath: executable, headless: true });
  page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
  page.setDefaultTimeout(15000);
  page.on('pageerror', error => errors.push(String(error)));
  page.on('request', event => {
    if (event.url().includes('/frontend/')) requests.push({ path: new URL(event.url()).pathname,
      authenticated: event.headers().authorization === 'Bearer ' + token });
  });
  await page.addInitScript(() => {
    const urls = new Set();
    const create = URL.createObjectURL.bind(URL), revoke = URL.revokeObjectURL.bind(URL);
    URL.createObjectURL = blob => { const url = create(blob); urls.add(url); return url; };
    URL.revokeObjectURL = url => { urls.delete(url); revoke(url); };
    Object.defineProperty(window, '__activityUrls', { value: urls });
  });
  await page.goto(input.address);
  if (!localOnly) {
    await page.locator('.authentication input[type=password]').fill(token);
    await page.locator('.authentication button').click();
  }
  await page.waitForFunction(() => document.querySelector('style[data-extension-id="game-activities"]'));
  await page.locator('a[href="#/dashboard"]').first().click();
  await page.locator('.activity-panel').getByRole('button', { name: '活动设置', exact: true }).click();
  await page.locator('.activity-game-row').first().waitFor();
  assert.equal(await page.locator('.activity-game-row').count(), 8);
  assert.deepEqual((await state()).selectedFeedIds, []);
  checks.push('the actual empty carousel opens eight game subscription controls');
  const row = page.locator('.activity-game-row[data-item-id="honkai-star-rail"]');
  await row.getByRole('switch').click();
  await page.locator('nexus-modal').getByRole('button', { name: '保存', exact: true }).click();
  const saved = await awaitState(value => value.selectedFeedIds.length === 1 && !value.refreshing &&
    value.feeds.some(feed => feed.game === 'honkai-star-rail' && feed.cacheState === 'fresh'));
  const selectedId = saved.selectedFeedIds[0];
  const detail = await json(base + '/invoke/read-feed', { input: { feedId: selectedId } });
  assert.ok(detail.feed.events.length > 0);
  checks.push('the actual save control commits the subscription and loads a real HTTPS feed');
  await page.locator('.activity-carousel[aria-busy="false"] .activity-hit').first().waitFor();
  await page.locator('.activity-hit').first().click();
  await page.locator('.activity-details').waitFor();
  assert.ok(await page.locator('.activity-details').getByText('数据来源：SRA', { exact: true }).isVisible());
  assert.equal(await page.locator('.activity-event').count(), detail.feed.events.length);
  await page.locator('.activity-details').getByText(detail.feed.events[0].title, { exact: true }).waitFor();
  assert.match(await page.locator('.activity-details .activity-help').first().textContent(), /未核验时间保留来源原值/);
  await page.screenshot({ path: path.join(input.output, 'activity-details.png') });
  checks.push('the actual details modal shows the saved source events and explicit time uncertainty');
  await page.keyboard.press('Escape');
  await page.locator('.activity-details').waitFor({ state: 'detached' });
  await page.locator('.activity-panel').getByRole('button', { name: '活动设置', exact: true }).click();
  await page.locator('.activity-game-row[data-item-id="genshin-impact"]').getByRole('switch').click();
  await page.locator('nexus-modal').getByRole('button', { name: '取消', exact: true }).click();
  await page.locator('.activity-settings').waitFor({ state: 'detached' });
  assert.equal((await state()).settingsRevision, saved.settingsRevision);
  assert.deepEqual((await state()).selectedFeedIds, saved.selectedFeedIds);
  checks.push('canceling a changed subscription draft preserves the committed revision');
  await page.locator('a[href="#/settings"]').first().click();
  await page.waitForFunction(() => !document.querySelector('.activity-panel')?.getClientRects().length);
  await setTimeout(250);
  const hiddenReads = requests.length; await setTimeout(5500);
  assert.equal(requests.length, hiddenReads);
  await page.locator('a[href="#/dashboard"]').first().click();
  await page.locator('.activity-hit').first().waitFor();
  assert.deepEqual((await state()).selectedFeedIds, saved.selectedFeedIds);
  checks.push('hiding and restoring the actual dashboard preserves the subscription');
  await enable(false);
  await page.waitForFunction(() => !document.querySelector('style[data-extension-id="game-activities"]') &&
    !document.querySelector('.activity-panel') && window.__activityUrls.size === 0);
  assert.equal((await request(base + '/invoke/read-state', { input: {} })).ok, false);
  checks.push('disable retires the old endpoint and releases every module image URL');
  await enable(true);
  await page.waitForFunction(() => document.querySelectorAll('style[data-extension-id="game-activities"]').length === 1);
  const renewed = (await json('/control/v1/extensions/frontend')).extensions.find(item => item.manifest.extensionId === 'game-activities');
  assert.notEqual(renewed.activationId, module.activationId);
  base = `/control/v1/extensions/game-activities/frontend/${renewed.activationId}`;
  assert.deepEqual((await state()).selectedFeedIds, saved.selectedFeedIds);
  assert.ok(requests.length > 0 && (localOnly || requests.every(item => item.authenticated)));
  assert.equal(errors.length, 0);
  checks.push('reactivation uses one new module owner and retains the saved feed');
  assert.deepEqual({ script: hash(process.argv[1]), browser: hash(executable) }, source);
  fs.writeFileSync(path.join(input.output, 'result.json'), JSON.stringify({ status: 'PASS', checks, requests, errors,
    source, browserVersion: browser.version(), scope: 'Actual Test Host original Activities ZIP and production Web; public feed GET only', network: localOnly ? 'loopback; LAN authorization excluded' : 'authenticated LAN' }, null, 2));
} catch (error) {
  if (page) await page.screenshot({ path: path.join(input.output, 'failure.png') }).catch(() => {});
  fs.writeFileSync(path.join(input.output, 'result.json'), JSON.stringify({ status: 'FAIL', checks, requests, errors,
    failure: String(error.stack ?? error).replaceAll(token, '[redacted]'), source }, null, 2));
  process.exitCode = 1;
} finally { await browser?.close(); }
