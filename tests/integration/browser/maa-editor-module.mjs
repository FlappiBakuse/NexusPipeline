import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const input = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const token = process.env[input.tokenEnvironment];
if (!token || !input.context?.bindingId || !path.isAbsolute(input.output) || !path.isAbsolute(input.modules))
  throw new Error('maa_browser_inputs_required');
const { chromium } = await import(pathToFileURL(path.join(input.modules, 'playwright-core/index.mjs')).href);
const executable = input.browser;
if (!path.isAbsolute(executable || '') || !fs.statSync(executable).isFile()) throw new Error('explicit_browser_required');
const hash = file => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
const sources = { script: hash(process.argv[1]), browser: hash(executable) };
const checks = [], requests = [], errors = [];
let browser, page, base;

async function json(route, body) {
  const response = await fetch(new URL(route, input.address), {
    method: body === undefined ? 'GET' : 'POST', redirect: 'error',
    headers: { Authorization: 'Bearer ' + token, ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }), signal: AbortSignal.timeout(15000)
  });
  assert.equal(response.ok, true, route + ':' + response.status + ':' + (response.ok ? '' : await response.text()));
  return response.json();
}
const current = () => json('/control/v1/projects/context', { format: 'nexus.control.project-command', version: 1,
  action: 'context', bindingId: input.context.bindingId });

async function choose(label, value) {
  const control = page.locator('nexus-select[aria-label=' + JSON.stringify(label) + ']');
  await control.locator('input[role=combobox]').click();
  await control.locator('[role=option][data-id=' + JSON.stringify(value) + ']').click();
}
async function confirm() {
  const dialog = page.locator('nexus-confirm-dialog');
  await dialog.locator('[data-action=confirm]').click();
  await dialog.waitFor({ state: 'detached' });
}
async function openBinding() {
  await page.locator('a[href="#/users"]').first().click();
  await page.locator('.automation-row').getByRole('button', { name: input.context.userName, exact: true }).click();
  const binding = page.locator('.binding[data-item-id=' + JSON.stringify(input.context.bindingId) + ']');
  await binding.locator('.binding-toggle').click();
  await binding.locator('.maa-editor').waitFor();
  await binding.getByRole('button', { name: '读取项目', exact: true }).click();
  await binding.getByRole('textbox', { name: 'Credentials / user', exact: true }).waitFor();
}
async function preview() {
  await page.locator('.maa-editor').getByRole('button', { name: '只读执行预览', exact: true }).click();
  await page.locator('.maa-output').filter({ hasText: '"canExecute": true' }).waitFor();
}
function selectSettled() {
  return new Promise(resolve => {
    let timer;
    const finish = value => {
      clearTimeout(timer);
      page.off('requestfinished', onFinished);
      page.off('requestfailed', onFailed);
      resolve(value);
    };
    const match = item => item.url().endsWith('/project/select') && item.method() === 'POST';
    const onFinished = item => { if (match(item)) finish(true); };
    const onFailed = item => { if (match(item)) finish(false); };
    page.on('requestfinished', onFinished);
    page.on('requestfailed', onFailed);
    timer = setTimeout(() => finish(undefined), 60000);
  });
}
async function save() {
  const settled = selectSettled();
  await page.locator('.maa-editor').getByRole('button', { name: '保存用户配置', exact: true }).click();
  await confirm();
  assert.notEqual(await settled, undefined, 'the save control must issue one project/select request');
}

try {
  const catalog = await json('/control/v1/extensions/frontend');
  const extension = catalog.extensions.find(item => item.manifest.extensionId === 'maa-framework');
  assert.ok(extension);
  base = '/control/v1/extensions/maa-framework/frontend/' + extension.activationId;
  browser = await chromium.launch({ executablePath: executable, headless: true, args: ['--no-sandbox'] });
  page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
  page.setDefaultTimeout(15000);
  page.on('pageerror', error => errors.push(String(error)));
  page.on('request', request => {
    if (request.url().includes('/frontend/')) requests.push({ path: new URL(request.url()).pathname,
      authenticated: request.headers().authorization === 'Bearer ' + token });
  });
  await page.goto(input.address);
  await page.locator('.authentication input[type=password]').fill(token);
  await page.locator('.authentication button').click();
  await page.waitForFunction(() => document.querySelector('style[data-extension-id="maa-framework"]'));
  assert.equal((await current()).selectionRevision, 0);
  checks.push('the actual signed original Maa and MaaS packages publish the authenticated production module');

  await openBinding();
  assert.equal(await page.getByRole('textbox', { name: 'Credentials / user', exact: true }).inputValue(), 'original-user');
  assert.equal(await page.locator('.maa-occurrence').count(), 1);
  assert.equal((await current()).selectionRevision, 0);
  checks.push('the actual binding editor discovers and previews its Host mounted original MXU profile without saving');

  await choose('添加任务条目', 'Daily');
  await page.locator('.maa-occurrence').nth(1).waitFor();
  const firstId = await page.locator('.maa-occurrence').first().getAttribute('data-item-id');
  await page.getByRole('textbox', { name: 'Daily · 重复次数', exact: true }).first().fill('2');
  await page.locator('.maa-occurrence').first().locator('nexus-drag-handle button').press('ArrowDown');
  assert.notEqual(await page.locator('.maa-occurrence').first().getAttribute('data-item-id'), firstId);
  await page.getByRole('textbox', { name: 'Credentials / user', exact: true }).fill('edited-user');
  await preview();
  assert.equal((await current()).selectionRevision, 0);
  checks.push('real keyboard ordering, independent occurrence addition, repeat and ordinary input produce only a draft preview');

  await save();
  await page.locator('.maa-editor').getByText('已保存', { exact: true }).waitFor();
  const saved = await current();
  assert.equal(saved.selectionRevision, 1);
  assert.equal(saved.selection.Profile.Tasks.length, 2);
  assert.equal(saved.selection.Profile.Tasks[1].Id, firstId);
  assert.equal(saved.selection.Profile.Tasks[1].RepeatCount, 2);
  assert.equal(saved.selection.Profile.GlobalOptions.Login.user, 'edited-user');
  checks.push('the actual save control commits the complete Host choice with original identities and expected revision');

  await page.getByRole('textbox', { name: 'Credentials / user', exact: true }).fill('lost-reply-user');
  await preview();
  let lost;
  await page.route('**/project/select', async route => {
    if (lost) { await route.continue(); return; }
    lost = route.request().postDataJSON();
    const result = await route.fetch();
    assert.equal(result.ok(), true);
    await route.abort('failed');
  });
  await save();
  await page.getByText('保存结果待确认，请保留草稿并读取原操作结果。', { exact: true }).waitFor();
  await page.getByRole('textbox', { name: 'Credentials / user', exact: true }).fill('later-unsaved-draft');
  await page.getByRole('button', { name: '读取原保存结果', exact: true }).click();
  await page.locator('.maa-editor').getByText('已保存', { exact: true }).waitFor();
  await page.unroute('**/project/select');
  const recovered = await current();
  assert.equal(recovered.selectionRevision, 2);
  assert.equal(recovered.selection.Profile.GlobalOptions.Login.user, 'lost-reply-user');
  assert.equal(lost.input.expectedSelectionRevision, 1);
  assert.equal(await page.getByRole('textbox', { name: 'Credentials / user', exact: true }).inputValue(), 'later-unsaved-draft');
  assert.equal(requests.filter(item => item.path.endsWith('/project/select')).length, 2);
  checks.push('an actual committed HTTP response loss resolves the original operation without a third write or losing the later draft');

  await choose('任务预设', 'DailyOnly');
  await page.getByRole('button', { name: '应用所选预设到草稿', exact: true }).click();
  await page.waitForFunction(() => document.querySelectorAll('.maa-occurrence').length === 1);
  await preview(); await save();
  await page.locator('.maa-editor').getByText('已保存', { exact: true }).waitFor();
  assert.equal((await current()).selectionRevision, 3);
  assert.equal((await current()).selection.Profile.Tasks.length, 1);
  checks.push('the production preset action uses the original installed compiler and saves independent editable tasks');

  await page.screenshot({ path: path.join(input.output, 'maa-binding-editor.png'), fullPage: true });
  assert.ok(requests.filter(item => item.path.includes('/project/')).every(item => item.authenticated));
  assert.ok(requests.some(item => item.path.endsWith('/project/page')));
  assert.ok(requests.some(item => item.path.endsWith('/project/result')));
  assert.deepEqual(errors, []);
  checks.push('the actual app CSP, frontend client and bounded preview pages serve the original production module');

  const settings = await json('/control/v1/settings');
  settings.saved.values.extensions = settings.saved.values.extensions.map(item => ({ ...item,
    enabled: item.extensionId === 'maa-framework' ? false : item.enabled }));
  await json('/control/v1/settings', { format: 'nexus.control.host-settings', version: 1, operationId: crypto.randomUUID(),
    expectedRevision: settings.saved.revision, values: settings.saved.values });
  const installed = (await json('/control/v1/extensions')).items.find(item => item.extensionId === 'maa-framework');
  const updated = await json('/control/v1/settings');
  await json('/control/v1/extensions/reconcile', { extensionId: 'maa-framework', expectedInstalledRevision: installed.installedRevision,
    expectedSettingsRevision: updated.saved.revision });
  await page.locator('.maa-editor').waitFor({ state: 'detached' });
  checks.push('retiring the original Maa activation removes its editor and releases the source preview');
  assert.equal(hash(process.argv[1]), sources.script); assert.equal(hash(executable), sources.browser);
  fs.writeFileSync(path.join(input.output, 'result.json'), JSON.stringify({ status: 'PASS', checks, requests, sources,
    qualification: 'Actual authenticated Host and original Maa/MaaS package browser editor with synthetic PI/configuration; no game, Agent or task execution' }, null, 2) + '\n');
} catch (error) {
  if (page) await page.screenshot({ path: path.join(input.output, 'failure.png'), fullPage: true }).catch(() => {});
  fs.writeFileSync(path.join(input.output, 'result.json'), JSON.stringify({ status: 'FAIL', checks, requests, sources, errors,
    error: String(error.stack || error), visibleText: page ? (await page.locator('body').innerText().catch(() => '')).slice(-6000) : '' }, null, 2) + '\n');
  process.exitCode = 1;
} finally { await browser?.close(); }
