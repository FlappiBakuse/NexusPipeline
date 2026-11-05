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
  const deadline = Date.now() + 15000;
  while (Date.now() < deadline) {
    const value = await state();
    if (predicate(value)) return value;
    await setTimeout(100);
  }
  throw new Error('wallpaper_state_timeout');
}
async function enable(enabled) {
  const settings = await json('/control/v1/settings');
  settings.saved.values.extensions = settings.saved.values.extensions.map(item => ({ ...item,
    enabled: item.extensionId === 'custom-wallpaper' ? enabled : item.enabled }));
  await json('/control/v1/settings', { format: 'nexus.control.host-settings', version: 1,
    operationId: crypto.randomUUID(), expectedRevision: settings.saved.revision, values: settings.saved.values });
  const installed = (await json('/control/v1/extensions')).items.find(item => item.extensionId === 'custom-wallpaper');
  const current = await json('/control/v1/settings');
  await json('/control/v1/extensions/reconcile', { extensionId: 'custom-wallpaper',
    expectedInstalledRevision: installed.installedRevision, expectedSettingsRevision: current.saved.revision });
}

function boundaryPng(original) {
  const result = Buffer.alloc(8388608);
  const offset = original.length - 12;
  original.copy(result, 0, 0, offset);
  const length = result.length - original.length - 12;
  result.writeUInt32BE(length, offset); result.write('npAD', offset + 4);
  const table = Array.from({ length: 256 }, (_, value) => {
    for (let index = 0; index < 8; index++) value = value & 1 ? 0xedb88320 ^ (value >>> 1) : value >>> 1;
    return value >>> 0;
  });
  let crc = 0xffffffff;
  for (const value of result.subarray(offset + 4, offset + 8 + length)) crc = table[(crc ^ value) & 255] ^ (crc >>> 8);
  result.writeUInt32BE((crc ^ 0xffffffff) >>> 0, offset + 8 + length);
  original.copy(result, offset + 12 + length, original.length - 12);
  return result;
}
try {
  const catalog = await json('/control/v1/extensions/frontend');
  const module = catalog.extensions.find(item => item.manifest.extensionId === 'custom-wallpaper');
  assert.ok(module);
  base = `/control/v1/extensions/custom-wallpaper/frontend/${module.activationId}`;
  if (!localOnly) {
    const anonymous = await request(base + '/assets/entry', undefined, false);
    const anonymousUpload = await fetch(new URL(base + '/upload/upload-asset', input.address), {
      method: 'POST', headers: { 'Content-Type': 'image/png' }, body: new Uint8Array([1]), signal: AbortSignal.timeout(15000) });
    assert.ok([401, 403].includes(anonymous.status));
    assert.ok([401, 403].includes(anonymousUpload.status));
    checks.push('anonymous module and raw upload refused');
  }
  browser = await chromium.launch({ executablePath: executable, headless: true });
  page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
  page.setDefaultTimeout(15000);
  page.on('pageerror', error => errors.push(String(error)));
  page.on('request', event => {
    if (event.url().includes('/frontend/') && /\/(upload|images)\//.test(event.url()))
      requests.push({ path: new URL(event.url()).pathname, authenticated: event.headers().authorization === 'Bearer ' + token,
        type: event.headers()['content-type'] ?? null });
  });
  await page.addInitScript(() => {
    const urls = new Set();
    const create = URL.createObjectURL.bind(URL), revoke = URL.revokeObjectURL.bind(URL);
    URL.createObjectURL = blob => { const url = create(blob); urls.add(url); return url; };
    URL.revokeObjectURL = url => { urls.delete(url); revoke(url); };
    Object.defineProperty(window, '__imageUrls', { value: urls });
  });
  await page.goto(input.address);
  if (!localOnly) {
    await page.locator('.authentication input[type=password]').fill(token);
    await page.locator('.authentication button').click();
  }
  await page.waitForFunction(() => !!document.querySelector('style[data-extension-id="custom-wallpaper"]'));
  await page.locator('a[href="#/settings"]').first().click();
  await page.locator('.wallpaper-settings > summary').click();
  const formats = await page.evaluate(() => ['image/png', 'image/jpeg', 'image/webp'].map((type, index) => {
    const canvas = document.createElement('canvas'); canvas.width = 32; canvas.height = 18;
    const context = canvas.getContext('2d'); context.fillStyle = ['#224466', '#664422', '#226644'][index];
    context.fillRect(0, 0, 32, 18);
    return { type, content: canvas.toDataURL(type).split(',')[1] };
  }));
  for (const [index, image] of formats.entries()) {
    await page.locator('.wallpaper-settings input[type=file]').setInputFiles({
      name: `browser-${index}.${['png', 'jpg', 'webp'][index]}`, mimeType: image.type, buffer: Buffer.from(image.content, 'base64') });
    await awaitState(value => value.assets.length === index + 1);
    await page.waitForFunction(() => !document.querySelector('.wallpaper-settings nexus-file-picker')?.disabled);
  }
  checks.push('actual settings uploaded PNG JPEG and WebP through the raw production transport');
  await page.locator('.wallpaper-list').scrollIntoViewIfNeeded();
  await page.waitForFunction(() => {
    const images = [...document.querySelectorAll('.wallpaper-list img')];
    return images.length === 3 && images.every(image => image.complete && image.naturalWidth === 32);
  });
  assert.ok(requests.length >= 6 && (localOnly || requests.every(item => item.authenticated)));
  checks.push('all three original image formats decoded through production pooled reads');
  await page.locator('.wallpaper-switches nexus-switch-setting').first().getByRole('switch').click();
  await awaitState(value => value.enabled && value.effectiveEnabled);
  await page.waitForFunction(() => document.querySelector('.wallpaper-appearance-image')?.style.backgroundImage.includes('blob:'));
  checks.push('the actual enable switch saved settings and displayed the wallpaper');
  const beforeDuplicate = await state();
  await page.locator('.wallpaper-settings input[type=file]').setInputFiles({
    name: 'duplicate.png', mimeType: 'image/png', buffer: Buffer.from(formats[0].content, 'base64') });
  await awaitState(value => value.revision > beforeDuplicate.revision);
  let saved = await state(); assert.equal(saved.assets.length, 3);
  checks.push('duplicate original content retains exactly three saved assets');
  const first = saved.assets[0].id;
  if (!localOnly) {
    const hidden = await request(base + '/images/read-asset/' + first, undefined, false);
    assert.ok([401, 403].includes(hidden.status));
  }
  assert.equal((await request(base + '/images/save-settings/' + first)).ok, false);
  assert.deepEqual((await state()).assets.map(item => item.id), saved.assets.map(item => item.id));
  checks.push(localOnly ? 'image reads cannot invoke a write operation' : 'image reads require authorization and cannot invoke a write operation');
  const boundary = boundaryPng(Buffer.from(formats[0].content, 'base64'));
  const sendImage = bytes => fetch(new URL(base + '/upload/upload-asset', input.address), {
    method: 'POST', headers: { Authorization: 'Bearer ' + token, 'Content-Type': 'image/png',
      'X-Nexus-Operation-Id': crypto.randomUUID(), 'X-Nexus-File-Name': 'boundary.png' },
    body: bytes, signal: AbortSignal.timeout(15000) });
  const accepted = await sendImage(boundary); assert.equal(accepted.status, 200);
  saved = await accepted.json(); assert.equal(saved.assets.length, 4);
  const largeId = crypto.createHash('sha256').update(boundary).digest('hex');
  const original = await request(base + '/images/read-asset/' + largeId);
  assert.equal(original.headers.get('content-type'), 'image/png');
  assert.equal(hashBytes(Buffer.from(await original.arrayBuffer())), largeId);
  assert.equal((await sendImage(Buffer.concat([boundary, Buffer.from([0])]))).status, 413);
  assert.equal((await state()).assets.length, 4);
  checks.push('exact eight MiB original PNG commits and reads while the next byte is refused without adding an asset');
  await page.locator('a[href="#/dashboard"]').first().click();
  await page.waitForFunction(() => !document.querySelector('.wallpaper-settings')?.getClientRects().length);
  await page.locator('a[href="#/settings"]').first().click();
  if (!await page.locator('.wallpaper-settings').evaluate(element => element.open))
    await page.locator('.wallpaper-settings > summary').click();
  assert.deepEqual((await state()).order, saved.order);
  checks.push('settings surface remount preserves saved images and order');
  await page.screenshot({ path: path.join(input.output, 'wallpaper-settings.png') });
  const beforeEmpty = await state(), beforeUploads = requests.filter(item => item.path.includes('/upload/')).length;
  await page.locator('.wallpaper-settings input[type=file]').setInputFiles([]);
  await page.waitForFunction(() => !document.querySelector('.wallpaper-settings nexus-file-picker')?.disabled);
  assert.equal(requests.filter(item => item.path.includes('/upload/')).length, beforeUploads);
  assert.equal((await state()).revision, beforeEmpty.revision);
  checks.push('empty file-picker selection issues no upload and preserves saved state');
  let heldUpload;
  await page.route('**/frontend/**/upload/upload-asset', route => { heldUpload = route; });
  const cancelled = page.waitForEvent('requestfailed', { predicate: request => request.url().includes('/upload/upload-asset') });
  await page.locator('.wallpaper-settings input[type=file]').setInputFiles({
    name: 'cancelled.png', mimeType: 'image/png', buffer: Buffer.from(formats[0].content, 'base64') });
  const uploadDeadline = Date.now() + 15000;
  while (!heldUpload) { if (Date.now() > uploadDeadline) throw Error('wallpaper_upload_barrier_missing'); await setTimeout(50); }
  await enable(false);
  await cancelled;
  await heldUpload.abort().catch(() => {});
  await page.unroute('**/frontend/**/upload/upload-asset');
  checks.push('activation retirement aborts a pending browser upload before Host acceptance');
  await page.waitForFunction(() => !document.querySelector('style[data-extension-id="custom-wallpaper"]') && window.__imageUrls.size === 0);
  assert.equal((await request(base + '/images/read-asset/' + first)).ok, false);
  checks.push('disable retires the old activation and releases every tracked object URL');
  await enable(true);
  await page.waitForFunction(() => document.querySelectorAll('style[data-extension-id="custom-wallpaper"]').length === 1);
  const renewed = (await json('/control/v1/extensions/frontend')).extensions.find(item => item.manifest.extensionId === 'custom-wallpaper');
  assert.notEqual(renewed.activationId, module.activationId);
  base = `/control/v1/extensions/custom-wallpaper/frontend/${renewed.activationId}`;
  assert.deepEqual((await state()).order, saved.order);
  assert.equal((await state()).revision, beforeEmpty.revision); assert.equal(errors.length, 0);
  checks.push('reactivation has one new browser owner and preserves committed wallpaper data');
  assert.deepEqual({ script: hash(process.argv[1]), browser: hash(executable) }, source);
  fs.writeFileSync(path.join(input.output, 'result.json'), JSON.stringify({ status: 'PASS', checks, requests, errors,
    scope: 'Actual Test Host original wallpaper ZIP with production Web surfaces in isolated Edge',
    network: localOnly ? 'loopback; LAN authorization excluded' : 'authenticated LAN',
    source, browserVersion: browser.version() }, null, 2));
} catch (error) {
  if (page) await page.screenshot({ path: path.join(input.output, 'failure.png') }).catch(() => {});
  fs.writeFileSync(path.join(input.output, 'result.json'), JSON.stringify({ status: 'FAIL', checks, requests, errors,
    failure: String(error.stack ?? error).replaceAll(token, '[redacted]'), source }, null, 2));
  process.exitCode = 1;
} finally { await browser?.close(); }

function hashBytes(bytes) { return crypto.createHash('sha256').update(bytes).digest('hex'); }
