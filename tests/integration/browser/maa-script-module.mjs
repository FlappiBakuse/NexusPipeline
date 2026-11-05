import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const input = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const token = process.env[input.tokenEnvironment];
if (!token || !input.context?.scriptId || !path.isAbsolute(input.output) || !path.isAbsolute(input.modules))
  throw Error('maa_script_browser_inputs_required');
const { chromium } = await import(pathToFileURL(path.join(input.modules, 'playwright-core/index.mjs')).href);
const executable = input.browser;
if (!path.isAbsolute(executable || '') || !fs.statSync(executable).isFile()) throw new Error('explicit_browser_required');
const hash = file => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
const sources = { script: hash(process.argv[1]), browser: hash(executable) };
const checks = [], requests = [], errors = [];
let browser, page;
async function context(script = true) {
  const response = await fetch(new URL('/control/v1/projects/context', input.address), {
    method: 'POST', headers: { Authorization: 'Bearer ' + token, 'Content-Type': 'application/json' },
    body: JSON.stringify({ format: 'nexus.control.project-command', version: 1, action: 'context',
      ...(script ? { scriptId: input.context.scriptId } : { bindingId: input.context.bindingId }) }), signal: AbortSignal.timeout(15000)
  });
  assert.equal(response.ok, true, await response.clone().text());
  return response.json();
}
async function open() {
  await page.locator('a[href="#/scripts"]').first().click();
  await page.locator('.automation-row[data-item-id=' + JSON.stringify(input.context.scriptId) + '] .entity-link').click();
  await page.locator('.maa-editor').waitFor();
  await page.getByRole('button', { name: '高级设置', exact: true }).click();
  await page.getByRole('button', { name: '读取项目', exact: true }).click();
  await page.getByRole('textbox', { name: 'Credentials / user', exact: true }).waitFor();
}
try {
  browser = await chromium.launch({ executablePath: executable, headless: true, args: ['--no-sandbox'] });
  page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
  page.setDefaultTimeout(30000);
  page.on('pageerror', error => errors.push(String(error)));
  page.on('request', request => {
    if (request.url().includes('/project/')) requests.push({ path: new URL(request.url()).pathname,
      authenticated: request.headers().authorization === 'Bearer ' + token, body: request.postDataJSON() });
  });
  await page.goto(input.address);
  await page.locator('.authentication input[type=password]').fill(token);
  await page.locator('.authentication button').click();
  await page.waitForFunction(() => document.querySelector('style[data-extension-id="maa-framework"]'));
  assert.equal((await context()).selectionRevision, 0);
  await open();
  assert.equal(await page.getByRole('textbox', { name: 'Credentials / user', exact: true }).inputValue(), 'original-user');
  assert.equal((await context()).selectionRevision, 0);
  checks.push('the installed original Maa editor reads physical script configuration through independent script authority');
  await page.getByRole('textbox', { name: 'Credentials / user', exact: true }).fill('script-independent-user');
  await page.getByRole('button', { name: '只读执行预览', exact: true }).click();
  await page.locator('.maa-output').filter({ hasText: '"canExecute": true' }).waitFor();
  assert.equal((await context()).selectionRevision, 0);
  await page.getByRole('button', { name: '保存脚本项目配置', exact: true }).click();
  await page.locator('nexus-confirm-dialog [data-action=confirm]').click();
  await page.locator('.maa-editor').getByText('已保存', { exact: true }).waitFor();
  const saved = await context();
  assert.equal(saved.selectionRevision, 1);
  assert.equal(saved.selection.Profile.GlobalOptions.Login.user, 'script-independent-user');
  assert.equal((await context(false)).selectionRevision, 0);
  checks.push('confirmed ordinary script choice persists separately without changing the binding selection');
  await page.locator('nexus-modal .editor-actions').getByRole('button', { name: '取消', exact: true }).click();
  await page.locator('.maa-editor').waitFor({ state: 'detached' });
  await open();
  assert.equal(await page.getByRole('textbox', { name: 'Credentials / user', exact: true }).inputValue(), 'script-independent-user');
  assert.equal((await context()).selectionRevision, 1);
  checks.push('reopening the actual script dialog restores the saved script project draft');
  const writes = requests.filter(item => item.path.endsWith('/select'));
  assert.equal(writes.length, 1);
  assert.equal(writes[0].body.context.surfaceId, 'automation.script.editor');
  assert.equal(writes[0].body.input.expectedScriptRevision, saved.scriptRevision);
  assert.equal(Object.hasOwn(writes[0].body.input, 'expectedBindingRevision'), false);
  assert.ok(requests.every(item => item.authenticated));
  assert.deepEqual(errors, []);
  await page.screenshot({ path: path.join(input.output, 'maa-script-editor.png'), fullPage: true });
  checks.push('authenticated production frontend requests preserve script identity and issue exactly one save');
  assert.equal(hash(process.argv[1]), sources.script); assert.equal(hash(executable), sources.browser);
  fs.writeFileSync(path.join(input.output, 'result.json'), JSON.stringify({ status: 'PASS', checks, requests, sources,
    qualification: 'Actual Host and original Maa/MaaS packages with synthetic source; script editing only, no game or project execution' }, null, 2) + '\n');
} catch (error) {
  if (page) await page.screenshot({ path: path.join(input.output, 'failure.png'), fullPage: true }).catch(() => {});
  fs.writeFileSync(path.join(input.output, 'result.json'), JSON.stringify({ status: 'FAIL', checks, requests, sources, errors,
    error: String(error.stack || error), visibleText: page ? (await page.locator('body').innerText().catch(() => '')).slice(-6000) : '' }, null, 2) + '\n');
  process.exitCode = 1;
} finally { await browser?.close(); }
