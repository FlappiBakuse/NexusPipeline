import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const input = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const token = process.env[input.tokenEnvironment];
if (!token || !input.context?.bindingId || !path.isAbsolute(input.output)) throw Error('retry_preview_inputs_required');
if (!path.isAbsolute(input.browser || '') || !fs.statSync(input.browser).isFile()) throw new Error('explicit_browser_required');
const { chromium } = await import(pathToFileURL(path.join(input.modules, 'playwright-core/index.mjs')).href);
const capture = input.context.capture;
const checks = [], errors = [], attempts = [], grants = [], frames = [];
let browser, page, hold = false, release, nextRelease, holdNext = false, run, cancellation;
async function json(route, body) {
  const response = await fetch(new URL(route, input.address), { method: body === undefined ? 'GET' : 'POST',
    headers: { Authorization: 'Bearer ' + token, ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }), signal: AbortSignal.timeout(15000) });
  if (!response.ok) throw Error(route + ':' + response.status);
  return response.json();
}
async function until(check, message, timeout = 90000) {
  const deadline = Date.now() + timeout;
  while (!await check()) { if (Date.now() >= deadline) throw Error(message); await delay(50); }
}
async function cancel() {
  if (!run || !cancellation) return;
  await json(`/control/v1/execution/${cancellation.operationId}/cancel/${crypto.randomUUID()}`,
    { format: 'nexus.control.execution-cancellation', version: 1, runId: run, attemptId: cancellation.attemptId });
}
try {
  browser = await chromium.launch({ executablePath: input.browser, headless: true });
  page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
  page.on('pageerror', error => errors.push(String(error)));
  page.on('response', async response => {
    if (new URL(response.url()).pathname === '/control/v1/media/previews' && response.ok())
      try { grants.push(await response.json()); } catch {}
  });
  await page.addInitScript(() => {
    const urls = new Set(), create = URL.createObjectURL.bind(URL), revoke = URL.revokeObjectURL.bind(URL);
    window.__previewCreated = 0;
    URL.createObjectURL = blob => { const url = create(blob); urls.add(url); window.__previewCreated++; return url; };
    URL.revokeObjectURL = url => { urls.delete(url); revoke(url); };
    Object.defineProperty(window, '__previewUrls', { value: urls });
  });
  await page.route('**/control/v1/media/previews/*/frame', async route => {
    try {
      const response = await route.fetch();
      const body = await response.body();
      const previewId = new URL(route.request().url()).pathname.split('/').at(-2);
      if (!response.ok()) fs.appendFileSync(path.join(input.output, 'preview-responses.jsonl'),
        JSON.stringify({ previewId, status: response.status(), body: body.toString().slice(0, 1024) }) + '\n');
      if (response.ok()) {
        assert.equal(body.readUInt32BE(20), 360);
        frames.push({ previewId, capturedAt: response.headers()['x-captured-at'], sha256: crypto.createHash('sha256').update(body).digest('hex') });
        if (hold) { hold = false; holdNext = true; await new Promise(resolve => { release = resolve; }); }
        else if (holdNext) { holdNext = false; await new Promise(resolve => { nextRelease = resolve; }); }
      }
      if (response.status() === 409 && JSON.parse(body.toString()).code === 'media_preview_unavailable')
        frames.push({ previewId, state: 'unavailable' });
      await route.fulfill({ response, body });
    } catch (error) {
      if (!/cancel|closed|abort|disposed|refused|reset/i.test(String(error))) errors.push(String(error));
    }
  });
  await page.goto(input.address);
  await page.waitForFunction(() => document.querySelector('style[data-extension-id="live-screenshot"]'));
  await page.locator('a[href="#/dispatch"]').first().click();
  const definitions = await json('/control/v1/automation'), settings = await json('/control/v1/settings');
  const operationId = crypto.randomUUID();
  const started = await json('/control/v1/execution/start/' + operationId, { format: 'nexus.control.execution-selection', version: 1,
    expectedCatalogRevision: definitions.catalogRevision, expectedSettingsRevision: settings.saved.revision,
    target: { kind: 'binding', bindingId: input.context.bindingId } });
  run = started.runId; attempts.push(started.attemptId); cancellation = { operationId, attemptId: started.attemptId };
  const image = page.locator(`.running-item[data-run-id="${run}"] .screenshot-image:visible`);
  if (capture) {
    await until(() => image.count().then(count => count === 1), 'first_native_preview_missing');
    const firstUrl = await image.getAttribute('src'); assert.ok(firstUrl?.startsWith('blob:'));
    checks.push('original ZIP displays the actual native first invocation at 360 pixels high');
    hold = true;
    await until(() => !!release, 'old_native_frame_not_held');
    const previous = new Set(grants.map(grant => grant.previewId));
    fs.writeFileSync(path.join(input.output, 'restart-request'), 'first frame held');
    await until(() => fs.existsSync(path.join(path.dirname(input.output), 'retry-worker.failed')), 'first_invocation_not_failed');
    await until(() => grants.some(grant => !previous.has(grant.previewId)), 'retry_preview_grant_missing');
    checks.push('actual target repetition creates a fresh preview grant within the same attempt');
    await until(() => !!nextRelease, 'retry_native_response_not_held');
    const created = await page.evaluate(() => window.__previewCreated);
    release(); release = undefined;
    await delay(750);
    assert.equal(await page.evaluate(() => window.__previewCreated), created);
    nextRelease(); nextRelease = undefined;
    await until(() => frames.some(frame => !previous.has(frame.previewId) && frame.sha256), 'retry_native_frame_missing');
    await until(async () => await image.count() === 1 && await image.getAttribute('src') !== firstUrl, 'retry_image_missing');
    assert.ok(grants.filter(grant => grant.runId === run).every(grant => grant.attemptId === attempts[0]));
    checks.push('late revoked preview creates no image URL; the released current native response supplies the replacement image');
  } else {
    await until(() => frames.some(frame => frame.state === 'unavailable'), 'first_unavailable_state_missing');
    assert.equal(await image.count(), 0);
    checks.push('safe no-target worker reports unavailable through the actual Host and production frontend');
    fs.writeFileSync(path.join(input.output, 'restart-request'), 'unavailable preview observed');
    await until(() => fs.existsSync(path.join(input.output, 'host-restarted')), 'actual_host_restart_missing');
    await until(async () => (await json('/control/v1/execution/active')).items.every(item => item.runId !== run),
      'stopped_run_remains_active');
    checks.push('normal Host interruption and same-port restart leave no successor with an unproven continuous budget');
    await until(() => page.locator('.running-item').count().then(count => count === 0), 'reconnected_preview_remains');
    assert.equal(await image.count(), 0);
    assert.ok(grants.every(grant => grant.attemptId === attempts[0]));
    cancellation = undefined;
    checks.push('production frontend reconnects and releases the interrupted preview without acquiring a successor grant');
  }
  await cancel();
  await until(() => page.locator('.running-item').count().then(count => count === 0), 'cancelled_retry_remains');
  await until(() => page.evaluate(() => window.__previewUrls.size === 0), 'retry_image_resources_remain');
  assert.deepEqual(errors, []);
  checks.push(capture ? 'cancelling the retried attempt releases all preview object URLs without browser errors'
    : 'actual Host restart leaves no preview object URLs or browser errors');
  fs.writeFileSync(path.join(input.output, 'result.json'), JSON.stringify({ status: 'PASS', checks, runId: run, attempts, grants, frames, errors,
    scope: capture ? 'Original ZIP, actual Host and native target repetition within one attempt; no cross-attempt image claim' : 'Original ZIP, actual Host interruption/restart, unavailable preview cleanup and budget-unknown decline; no successor/image claim' }, null, 2));
} catch (error) {
  await page?.screenshot({ path: path.join(input.output, 'failure.png') }).catch(() => {});
  fs.writeFileSync(path.join(input.output, 'result.json'), JSON.stringify({ status: 'FAIL', checks, runId: run, attempts, grants, frames, errors,
    failure: String(error.stack ?? error).replaceAll(token, '[redacted]') }, null, 2));
  process.exitCode = 1;
} finally {
  release?.(); nextRelease?.();
  try { await cancel(); } catch {}
  await browser?.close();
}
