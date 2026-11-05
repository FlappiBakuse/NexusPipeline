import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const input = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const token = process.env[input.tokenEnvironment];
if (!token || input.context?.bindings?.length !== 2 || !path.isAbsolute(input.output)) throw Error('screenshot_inputs_required');
const { chromium } = await import(pathToFileURL(path.join(input.modules, 'playwright-core/index.mjs')).href);
const executable = input.browser;
if (!path.isAbsolute(executable || '') || !fs.statSync(executable).isFile()) throw new Error('explicit_browser_required');
const hash = file => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
const sources = { script: hash(process.argv[1]), browser: hash(executable) };
const checks = [], errors = [], frames = [], grants = new Map(), runs = [], requests = [], calls = [];
let browser, page, slow = false;
const heldFrames = [];
let fault; const faults = [], observerFailures = [], httpFailures = [];
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
async function json(route, body) {
  const call = { route, started: Date.now() }; calls.push(call);
  let response;
  try { response = await fetch(new URL(route, input.address), { method: body === undefined ? 'GET' : 'POST',
    headers: { Authorization: 'Bearer ' + token, ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }), signal: AbortSignal.timeout(20000) }); }
  catch (error) { call.error = String(error); throw Error(route + ':' + error); }
  finally { call.elapsed = Date.now() - call.started; }
  call.status = response.status;
  if (!response.ok) throw Error(route + ':' + response.status + ':' + await response.text());
  return response.json();
}
async function until(check, message, timeout = 30000) {
  const deadline = Date.now() + timeout;
  while (!await check()) { if (Date.now() > deadline) throw Error(message); await delay(50); }
}
async function enabled(value) {
  const settings = await json('/control/v1/settings');
  settings.saved.values.extensions = settings.saved.values.extensions.map(item => ({ ...item,
    enabled: item.extensionId === 'live-screenshot' ? value : item.enabled }));
  await json('/control/v1/settings', { format: 'nexus.control.host-settings', version: 1,
    operationId: crypto.randomUUID(), expectedRevision: settings.saved.revision, values: settings.saved.values });
  const catalog = await json('/control/v1/extensions');
  const installed = catalog.items.find(item => item.extensionId === 'live-screenshot');
  const current = await json('/control/v1/settings');
  await json('/control/v1/extensions/reconcile', { extensionId: 'live-screenshot',
    expectedInstalledRevision: installed.installedRevision, expectedSettingsRevision: current.saved.revision });
}
async function cancel(run) {
  return json(`/control/v1/execution/${run.operationId}/cancel/${crypto.randomUUID()}`,
    { format: 'nexus.control.execution-cancellation', version: 1, runId: run.runId, attemptId: run.attemptId });
}
try {
  browser = await chromium.launch({ executablePath: executable, headless: true });
  page = await browser.newPage({ viewport: { width: 1280, height: 1000 } });
  page.setDefaultTimeout(30000); page.on('pageerror', error => errors.push(String(error)));
  page.on('response', async response => {
    if (response.status() >= 400) httpFailures.push({ path: new URL(response.url()).pathname, status: response.status(), at: Date.now() });
    if (new URL(response.url()).pathname === '/control/v1/media/previews' && response.ok()) {
      try { const grant = await response.json(); grants.set(grant.previewId, grant); }
      catch (error) { observerFailures.push(String(error)); }
    }
  });
  await page.route('**/control/v1/media/previews/*/frame', async route => {
    const previewId = new URL(route.request().url()).pathname.split('/').at(-2);
    const started = Date.now(); requests.push({ previewId, started });
    try {
      const response = await route.fetch();
      const bytes = await response.body();
      if (response.ok()) {
        const capturedAt = response.headers()['x-captured-at'];
        frames.push({ previewId, started, received: Date.now(), capturedAt,
          width: bytes.readUInt32BE(16), height: bytes.readUInt32BE(20), sha256: hashBytes(bytes) });
        if (slow) await new Promise(resolve => heldFrames.push(resolve));
      }
      if (response.ok() && fault && grants.get(previewId)?.runId === runs[0]?.runId) {
        const injected = fault; fault = undefined; faults.push(injected);
        if (injected === 'disconnect') { await route.abort('connectionreset'); return; }
        await route.fulfill({ response, body: bytes.subarray(0, 33),
          headers: { ...response.headers(), 'content-length': '33' } }); return;
      }
      await route.fulfill({ response, body: bytes });
    } catch (error) {
      if (!/cancel|closed|abort|disposed/i.test(String(error))) errors.push(String(error));
    }
  });
  await page.goto(input.address);
  await page.waitForFunction(() => document.querySelector('style[data-extension-id="live-screenshot"]'));
  await page.locator('a[href="#/dispatch"]').first().click();
  for (const bindingId of input.context.bindings) {
    const definitions = await json('/control/v1/automation'), settings = await json('/control/v1/settings');
    const operationId = crypto.randomUUID();
    const result = await json('/control/v1/execution/start/' + operationId, { format: 'nexus.control.execution-selection', version: 1,
      expectedCatalogRevision: definitions.catalogRevision, expectedSettingsRevision: settings.saved.revision,
      target: { kind: 'binding', bindingId } });
    runs.push({ operationId, runId: result.runId, attemptId: result.attemptId });
  }
  await until(() => page.locator('.running-item .screenshot-image:visible').count().then(count => count === 2), 'two_native_previews_missing');
  for (const [index, run] of runs.entries()) {
    const image = page.locator(`.running-item[data-run-id="${run.runId}"] .screenshot-image`);
    assert.deepEqual(await image.evaluate(node => [node.naturalWidth, node.naturalHeight]), input.context.dimensions[index]);
    if (input.context.pixels[index]) assert.deepEqual(await image.evaluate(node => {
      const canvas = document.createElement('canvas'); canvas.width = node.naturalWidth; canvas.height = node.naturalHeight;
      const context = canvas.getContext('2d'); context.drawImage(node, 0, 0);
      return Array.from(context.getImageData(Math.floor(canvas.width * .75), Math.floor(canvas.height * .5), 1, 1).data).slice(0, 3);
    }), input.context.pixels[index]);
  }
  assert.ok(frames.every(frame => frame.width > 0 && frame.height > 0 && frame.height <= 360 &&
    Number.isFinite(Date.parse(frame.capturedAt)) && Date.parse(frame.capturedAt) <= frame.received));
  checks.push('actual native frames reach distinct production surfaces at their expected preview dimensions with Host capture time');
  const firstCount = frames.length;
  await until(() => frames.length >= firstCount + 2, 'five_second_samples_missing');
  for (const run of runs) {
    const samples = frames.filter(frame => grants.get(frame.previewId)?.runId === run.runId);
    assert.ok(samples.length >= 2);
    assert.ok(samples.every(frame => grants.get(frame.previewId).attemptId === run.attemptId));
    for (let index = 1; index < samples.length; index++) assert.ok(samples[index].started - samples[index - 1].started >= 4500);
  }
  checks.push('five second sampling binds each grant to its original run and attempt');
  slow = true;
  await until(() => heldFrames.length === 2, 'two_slow_frames_missing');
  const heldCount = requests.length;
  await delay(5200); assert.equal(requests.length, heldCount);
  await page.locator('a[href="#/users"]').first().click();
  for (const release of heldFrames.splice(0)) release();
  await delay(7500); assert.equal(requests.length, heldCount);
  checks.push('slow actual responses never overlap and hidden surfaces reject late frames without new reads');
  slow = false;
  await page.locator('a[href="#/dispatch"]').first().click();
  await until(() => page.locator('.running-item .screenshot-image:visible').count().then(count => count === 2), 'restored_frames_missing');
  await enabled(false);
  await page.waitForFunction(() => !document.querySelector('style[data-extension-id="live-screenshot"]'));
  await delay(1000); const stopped = requests.length; await delay(5500); assert.equal(requests.length, stopped);
  await enabled(true);
  await until(() => page.locator('.running-item .screenshot-image:visible').count().then(count => count === 2), 'reactivated_frames_missing');
  checks.push('disable stops requests and renewed activation obtains two new current frames');
  for (const injected of ['decode', 'disconnect']) {
    const count = faults.length; fault = injected;
    await until(() => faults.length > count, injected + '_fault_missing');
    const image = page.locator(`.running-item[data-run-id="${runs[0].runId}"] .screenshot-image:visible`);
    await until(() => image.count().then(value => value === 0), injected + '_frame_not_released');
    await until(() => image.count().then(value => value === 1), injected + '_recovery_missing');
    checks.push('actual ' + injected + ' failure releases its frame and a later current sample recovers');
  }
  const cancelStarted = Date.now();
  for (const run of runs) await cancel(run);
  const active = await json('/control/v1/execution/active');
  for (const run of runs) {
    const stopping = active.items.find(item => item.runId === run.runId && item.attemptId === run.attemptId);
    if (stopping) { assert.equal(stopping.cancellationRequested, true); assert.equal(stopping.canCancel, false); }
  }
  await until(() => page.locator('.running-item .screenshot-image').count().then(count => count === 0), 'canceled_previews_remain', 5000);
  const stoppedAt = requests.length; await delay(5500); assert.equal(requests.length, stoppedAt);
  await until(() => page.locator('.running-item').count().then(count => count === 0), 'canceled_runs_remain', 90000);
  calls.push({ route: 'cancel-to-safe-retirement', elapsed: Date.now() - cancelStarted });
  await delay(1000); const finished = requests.length; await delay(5500); assert.equal(requests.length, finished);
  checks.push('cancel removes both run surfaces and stops preview requests');
  assert.equal(errors.length, 0);
  assert.deepEqual({ script: hash(process.argv[1]), browser: hash(executable) }, sources);
  await page.screenshot({ path: path.join(input.output, 'completed.png') });
  checks.push('production browser closes without unhandled errors and source bytes remain unchanged');
  fs.writeFileSync(path.join(input.output, 'result.json'), JSON.stringify({ status: 'PASS', checks, frames,
    requests, grants: [...grants.values()], runs, sources, errors, calls, faults, observerFailures, httpFailures,
    scope: 'Original plugin ZIP, actual loopback Test Host and production browser with controlled native window/Android sources; no vendor installation claim' }, null, 2));
} catch (error) {
  let activeAtFailure;
  try { activeAtFailure = await json('/control/v1/execution/active'); } catch {}
  await page?.screenshot({ path: path.join(input.output, 'failure.png') }).catch(() => {});
  fs.writeFileSync(path.join(input.output, 'result.json'), JSON.stringify({ status: 'FAIL', checks, frames, requests,
    runs, sources, errors, calls, faults, observerFailures, httpFailures, activeAtFailure, failure: String(error).replaceAll(token, '[redacted]') }, null, 2));
  process.exitCode = 1;
} finally {
  for (const release of heldFrames.splice(0)) release();
  for (const run of runs) { try { await cancel(run); } catch {} }
  for (const marker of input.context.markers) fs.writeFileSync(marker + '.main.release', 'release');
  await browser?.close();
}
function hashBytes(bytes) { return crypto.createHash('sha256').update(bytes).digest('hex'); }
