import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const input = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const token = process.env[input.tokenEnvironment];
if (!token || !input.context?.bindingId || !path.isAbsolute(input.context.marker) || !path.isAbsolute(input.output))
  throw new Error('maa_initialization_browser_inputs_required');
const { chromium } = await import(pathToFileURL(path.join(input.modules, 'playwright-core/index.mjs')).href);
const executable = input.browser;
if (!path.isAbsolute(executable || '') || !fs.statSync(executable).isFile()) throw new Error('explicit_browser_required');
const hash = file => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
const sources = { script: hash(process.argv[1]), browser: hash(executable) };
const checks = [], errors = [], starts = [];
let browser, page;
async function choose(label, option) {
  const control = page.locator('nexus-select[aria-label=' + JSON.stringify(label) + ']');
  await control.locator('input[role=combobox]').click();
  await control.getByRole('option', { name: option, exact: true }).click();
}
async function until(check) {
  const deadline = Date.now() + 90000;
  while (!await check()) {
    if (Date.now() >= deadline) throw new Error('initialization_evidence_timeout');
    await new Promise(resolve => setTimeout(resolve, 100));
  }
}
try {
  browser = await chromium.launch({ executablePath: executable, headless: true, args: ['--no-sandbox'] });
  page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
  page.setDefaultTimeout(30000);
  page.on('pageerror', error => errors.push(String(error)));
  page.on('request', request => {
    if (request.method() === 'POST' && new URL(request.url()).pathname.startsWith('/control/v1/execution/start/'))
      starts.push({ operationId: new URL(request.url()).pathname.split('/').at(-1), body: request.postDataJSON() });
  });
  await page.goto(input.address);
  await page.locator('.authentication input[type=password]').fill(token);
  await page.locator('.authentication button').click();
  await page.waitForFunction(() => document.querySelector('style[data-extension-id="maa-framework"]'));
  await page.locator('a[href="#/users"]').first().click();
  await page.locator('.automation-row').getByRole('button', { name: input.context.userName, exact: true }).click();
  const binding = page.locator('.binding[data-item-id=' + JSON.stringify(input.context.bindingId) + ']');
  await binding.locator('.binding-toggle').click();
  await binding.locator('.maa-editor').waitFor();
  await page.getByRole('textbox', { name: '原生 DLL 相对目录', exact: true }).fill('native');
  await binding.getByRole('button', { name: '读取项目', exact: true }).click();
  await binding.getByRole('textbox', { name: 'Credentials / user', exact: true }).waitFor();
  await binding.getByRole('button', { name: '只读执行预览', exact: true }).click();
  await page.locator('.maa-output').filter({ hasText: '"canExecute": true' }).waitFor();
  await binding.getByRole('button', { name: '保存用户配置', exact: true }).click();
  await page.locator('nexus-confirm-dialog [data-action=confirm]').click();
  await binding.getByText('已保存', { exact: true }).waitFor();
  assert.equal(fs.existsSync(input.context.marker), false);
  checks.push('actual editor preview and save preserve the selected pretask without starting it');

  await page.locator('nexus-modal[open]').getByRole('button', { name: '关闭', exact: true }).click();
  await page.locator('a[href="#/dispatch"]').first().click();
  await choose('目标类型', '项目初始化');
  const target = page.locator('nexus-select[aria-label="脚本实例"]');
  await target.locator('input[role=combobox]').click();
  await target.getByRole('option').filter({ hasText: input.context.userName }).click();
  const start = page.locator('.dispatch-targets').getByRole('button', { name: '执行初始化', exact: true });
  const planned = page.waitForResponse(response => new URL(response.url()).pathname === '/control/v1/execution/preview');
  await start.click();
  const plan = await (await planned).json();
  assert.equal(plan.hasRunnable, true, JSON.stringify(plan));
  const dialog = page.locator('nexus-confirm-dialog').filter({ hasText: '初始化可能修改脚本环境' });
  await dialog.locator('[data-action=cancel]').click();
  assert.equal(fs.existsSync(input.context.marker), false);
  assert.equal(starts.length, 0);
  checks.push('cancelling the real initialization confirmation sends no start request and causes no pretask side effect');

  await start.click();
  await dialog.locator('[data-action=confirm]').click();
  await until(() => fs.existsSync(input.context.marker));
  await page.locator('.dispatch-running').getByText('运行中', { exact: false }).first().waitFor();
  await until(async () => {
    assert.equal(starts.length, 1);
    const response = await page.request.get(input.address + 'control/v1/operations/' + starts[0].operationId,
      { headers: { Authorization: 'Bearer ' + token } });
    assert.equal(response.ok(), true);
    return (await response.json()).status === 'completed';
  });
  await page.waitForFunction(() => document.querySelector('.running-heading strong')?.textContent?.includes('(0)'));
  assert.deepEqual(fs.readFileSync(input.context.marker, 'utf8').trim().split('\n'), ['initialized']);
  assert.equal(starts.length, 1);
  assert.deepEqual(errors, []);
  checks.push('explicit confirmation admits one initialization and the Host finishes its owned pretask');
  await page.screenshot({ path: path.join(input.output, 'initialization-completed.png'), fullPage: true });
  assert.equal(hash(process.argv[1]), sources.script); assert.equal(hash(executable), sources.browser);
  fs.writeFileSync(path.join(input.output, 'result.json'), JSON.stringify({ status: 'PASS', checks, sources,
    qualification: 'Actual authenticated Host and installed Maa/MaaS with controlled pretask; no game execution' }, null, 2) + '\n');
} catch (error) {
  if (page) await page.screenshot({ path: path.join(input.output, 'failure.png'), fullPage: true }).catch(() => {});
  fs.writeFileSync(path.join(input.output, 'result.json'), JSON.stringify({ status: 'FAIL', checks, sources, errors,
    error: String(error.stack || error), visibleText: page ? (await page.locator('body').innerText().catch(() => '')).slice(-6000) : '' }, null, 2) + '\n');
  process.exitCode = 1;
} finally { await browser?.close(); }
