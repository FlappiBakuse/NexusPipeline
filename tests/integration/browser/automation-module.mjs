import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const input = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const token = process.env[input.tokenEnvironment];
if (!token || !input.context?.queueId || !path.isAbsolute(input.browser || '') || !fs.statSync(input.browser).isFile())
  throw new Error('Explicit owned queue fixture and browser required');
const { chromium } = await import(pathToFileURL(path.join(input.modules, 'playwright-core/index.mjs')));
const checks = [], failures = [], errors = [];
const started = performance.now();
let browser;
async function snapshot() {
  const response = await fetch(new URL('/control/v1/automation', input.address), {
    headers: { Authorization: 'Bearer ' + token }, signal: AbortSignal.timeout(10000)
  });
  assert(response.ok); return response.json();
}
try {
  const before = await snapshot();
  const queue = before.bundle.queues.find(item => item.id === input.context.queueId);
  assert(queue, 'The caller must create the isolated queue');
  browser = await chromium.launch({ executablePath: input.browser, headless: true });
  const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
  page.setDefaultTimeout(20000); page.on('pageerror', error => errors.push(String(error)));
  await page.goto(input.address);
  await page.locator('.authentication').waitFor({ state: 'detached' });
  await page.locator('a[href="#/queues"]').first().click();
  for (let ordinal = 1; ordinal <= 2; ordinal++) {
    const name = queue.name + ' ' + ordinal;
    await page.locator(`[data-item-id="${queue.id}"]`).getByRole('button', { name: '编辑队列', exact: true }).click();
    const dialog = page.locator('nexus-modal').filter({ has: page.getByRole('textbox', { name: '队列名称', exact: true }) });
    await dialog.getByRole('textbox', { name: '队列名称', exact: true }).fill(name);
    await dialog.getByRole('button', { name: '保存', exact: true }).click();
    await dialog.waitFor({ state: 'detached' });
    const after = await snapshot();
    assert.equal(after.bundle.queues.find(item => item.id === queue.id)?.name, name);
    assert.equal(after.catalogRevision, before.catalogRevision + ordinal);
    checks.push({ name: 'consecutive queue save ' + ordinal, status: 'PASS' });
  }
  assert.deepEqual(errors, []);
} catch (error) { failures.push(error.stack || String(error)); process.exitCode = 1; }
finally { await browser?.close(); }
const report = { status: failures.length || !checks.length ? 'FAIL' : 'PASS', scope: 'Actual isolated Host queue editor',
  command: process.argv, checks, failures, errors, elapsedMilliseconds: performance.now() - started };
fs.writeFileSync(path.join(input.output, 'result.json'), JSON.stringify(report, null, 2) + '\n');
if (report.status !== 'PASS') process.exitCode = 1;
