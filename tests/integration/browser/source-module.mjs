import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const input = JSON.parse(fs.readFileSync(process.argv[2], 'utf8')), token = process.env[input.tokenEnvironment];
if (!token || !path.isAbsolute(input.context?.directory || '') || !path.isAbsolute(input.output)) throw Error('Source browser inputs required');
if (!path.isAbsolute(input.browser || '') || !fs.statSync(input.browser).isFile()) throw new Error('explicit_browser_required');
const { chromium } = await import(pathToFileURL(path.join(input.modules, 'playwright-core/index.mjs')).href);
const digest = file => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
const directory = input.context.directory;
const files = ['BAAH.exe', 'BAAH_CONFIGS/one.json', 'BAAH_CONFIGS/two.json', 'DATA/CONFIGS/software_config.json'];
const hashes = Object.fromEntries(files.map(file => [file, digest(path.join(directory, file))]));
const checks = [], errors = [], requests = [];
let browser, page;
async function catalog() {
  const response = await fetch(new URL('/control/v1/automation', input.address), { headers: { Authorization: 'Bearer ' + token } });
  assert.equal(response.ok, true); return response.json();
}
try {
  browser = await chromium.launch({ executablePath: input.browser,
    headless: true, args: ['--no-sandbox'] });
  page = await browser.newPage({ viewport: { width: 1280, height: 900 } }); page.setDefaultTimeout(20000);
  page.on('pageerror', error => errors.push(String(error)));
  page.on('request', request => {
    if (request.url().includes('/automation/scripts/discover')) requests.push(request.postDataJSON());
  });
  await page.goto(input.address);
  await page.locator('.authentication input[type=password]').fill(token);
  await page.locator('.authentication button').click();
  await page.locator('.authentication').waitFor({ state: 'detached' });
  await page.getByText('一切准备就绪', { exact: true }).waitFor();
  await page.locator('.navigation-progress').waitFor({ state: 'detached' });
  await page.locator('a[href="#/scripts"]').first().click();
  await page.getByRole('button', { name: '新建脚本实例', exact: true }).click();
  const choice = page.locator('.type-choice').filter({ hasText: '蔚蓝档案爱丽丝助手' });
  await choice.waitFor(); assert.equal(await choice.isDisabled(), true);
  await page.getByText('请在 Host 本机创建专项脚本实例。', { exact: true }).waitFor();
  const local = new URL(input.address); local.hostname = '127.0.0.1';
  await page.goto(local.href);
  await page.getByText('一切准备就绪', { exact: true }).waitFor();
  await page.locator('.navigation-progress').waitFor({ state: 'detached' });
  await page.locator('a[href="#/scripts"]').first().click();
  await page.getByRole('button', { name: '新建脚本实例', exact: true }).click();
  await choice.click();
  await page.locator('nexus-text-input[aria-label="脚本名称"] input').fill('Source discovery acceptance');
  const root = page.locator('nexus-path-picker[aria-label="脚本根目录"] input');
  const save = page.locator('.editor-actions').getByRole('button', { name: '保存', exact: true });
  const discover = async rootPath => {
    const response = page.waitForResponse(item => item.url().includes('/automation/scripts/discover') &&
      item.request().postDataJSON()?.rootPath === rootPath);
    await root.fill(rootPath);
    await root.press('Tab');
    return response;
  };
  const found = await discover(directory);
  assert.ok(found.ok());
  assert.deepEqual((await found.json()).discovery.candidates.map(item => item.arguments), [['one.json'], ['two.json']]);
  assert.equal((await catalog()).bundle.scripts.length, 0);
  checks.push('The actual installed BAAH package discovers both configurations without publishing a draft');
  assert.equal((await discover(path.join(directory, 'absent'))).ok(), false);
  assert.equal(await save.isDisabled(), true);
  checks.push('Changing the directory invalidates the previously selected executable and configuration');
  assert.ok((await discover(directory)).ok());
  await page.getByRole('switch', { name: '强制关闭', exact: true }).click();
  await save.click();
  await page.locator('.automation-row').filter({ hasText: 'Source discovery acceptance' }).waitFor();
  const saved = await catalog(); assert.equal(saved.bundle.scripts.length, 1);
  const script = saved.bundle.scripts[0];
  assert.deepEqual(script.arguments, ['one.json']); assert.equal(script.executable.relative, 'BAAH.exe');
  assert.deepEqual(script.configurationPaths.map(item => item.relative), ['BAAH_CONFIGS/one.json', 'DATA/CONFIGS/software_config.json']);
  assert.equal(script.adapterResources.length, 2); assert.equal(saved.bundle.bindings.length, 0);
  checks.push('Saving creates one explicit specialized instance with matching paths and resources and no execution binding');
  assert.deepEqual(errors, []);
  for (const file of files) assert.equal(digest(path.join(directory, file)), hashes[file]);
  checks.push('Remote creation exposes the local-only boundary; local discovery and definition creation preserve all original script bytes');
  await page.screenshot({ path: path.join(input.output, 'source-created.png'), fullPage: true });
  fs.writeFileSync(path.join(input.output, 'result.json'), JSON.stringify({ status: 'PASS', checks, requests, hashes }, null, 2) + '\n');
} catch (error) {
  if (page) await page.screenshot({ path: path.join(input.output, 'failure.png'), fullPage: true }).catch(() => {});
  fs.writeFileSync(path.join(input.output, 'result.json'), JSON.stringify({ status: 'FAIL', checks, requests, errors,
    error: String(error.stack || error), visibleText: page ? (await page.locator('body').innerText().catch(() => '')).slice(-6000) : '' }, null, 2) + '\n');
  process.exitCode = 1;
} finally { await browser?.close(); }
