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
const checks = [], errors = [];
let starts = 0;
let browser, page;
try {
  browser = await chromium.launch({ executablePath: executable, headless: true, args: ['--no-sandbox'] });
  page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
  page.setDefaultTimeout(30000);
  page.on('pageerror', error => errors.push(String(error)));
  page.on('request', request => {
    if (request.method() === 'POST' && new URL(request.url()).pathname.startsWith('/control/v1/execution/start/'))
      starts++;
  });
  await page.goto(input.address);
  await page.locator('.authentication input[type=password]').fill(token);
  await page.locator('.authentication button').click();
  await page.waitForFunction(() => document.querySelector('style[data-extension-id="maa-framework"]'));
  await page.locator('a[href="#/users"]').first().click();
  const account = page.locator('.automation-row').filter({
    has: page.getByRole('heading', { name: input.context.userName, exact: true })
  });
  await account.getByRole('button', { name: '用户管理', exact: true }).click();
  const binding = page.locator('.binding[data-item-id=' + JSON.stringify(input.context.bindingId) + ']');
  await binding.locator('.binding-entry').click();
  const detail = page.locator('.binding-detail[data-draft-item=' + JSON.stringify(input.context.bindingId) + ']');
  await detail.locator('.edit-configuration').click();
  const editor = page.locator('.maa-editor');
  await editor.waitFor();
  await page.getByRole('textbox', { name: '原生 DLL 相对目录', exact: true }).fill('native');
  await editor.getByRole('button', { name: '读取项目', exact: true }).click();
  await editor.getByRole('textbox', { name: 'Credentials / user', exact: true }).waitFor();
  await editor.getByRole('button', { name: '只读执行预览', exact: true }).click();
  await page.locator('.maa-output').filter({ hasText: '"canExecute": true' }).waitFor();
  await editor.getByRole('button', { name: '保存用户配置', exact: true }).click();
  await page.locator('nexus-confirm-dialog [data-action=confirm]').click();
  await editor.getByText('已保存', { exact: true }).waitFor();
  assert.equal(fs.existsSync(input.context.marker), false);
  checks.push('actual editor preview and save preserve the selected pretask without starting it');

  const close = 'dialog > .panel > header > .close';
  await page.locator('nexus-modal').filter({ has: editor }).locator(close).click();
  await editor.waitFor({ state: 'detached' });
  await page.locator('nexus-modal').filter({ has: detail }).locator(close).click();
  await page.locator('a[href="#/dispatch"]').first().click();
  const targets = await page.locator('nexus-select[aria-label="目标类型"]').evaluate(control => control.options.map(option => option.label));
  assert.deepEqual(targets, ['脚本实例', '调度队列']);
  assert.equal(fs.existsSync(input.context.marker), false);
  assert.equal(starts, 0);
  assert.deepEqual(errors, []);
  checks.push('the run center offers only scripts and queues after saving the project selection');
  await page.screenshot({ path: path.join(input.output, 'dispatch-targets.png'), fullPage: true });
  assert.equal(hash(process.argv[1]), sources.script); assert.equal(hash(executable), sources.browser);
  fs.writeFileSync(path.join(input.output, 'result.json'), JSON.stringify({ status: 'PASS', checks, sources,
    qualification: 'Actual authenticated Host and installed Maa/MaaS with controlled pretask; no game execution' }, null, 2) + '\n');
} catch (error) {
  if (page) await page.screenshot({ path: path.join(input.output, 'failure.png'), fullPage: true }).catch(() => {});
  fs.writeFileSync(path.join(input.output, 'result.json'), JSON.stringify({ status: 'FAIL', checks, sources, errors,
    error: String(error.stack || error), visibleText: page ? (await page.locator('body').innerText().catch(() => '')).slice(-6000) : '' }, null, 2) + '\n');
  process.exitCode = 1;
} finally { await browser?.close(); }
