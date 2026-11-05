import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const input = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
if (!input.context?.uninstalledExtensionId) throw new Error('Explicit uninstalled catalog fixture required');
const { chromium } = await import(pathToFileURL(path.join(input.modules, 'playwright-core/index.mjs')));
const checks = [], errors = [];
let browser;
async function read(route) {
  const response = await fetch(new URL('/control/v1/' + route, input.address), { redirect: 'error',
    headers: { Authorization: 'Bearer ' + process.env[input.tokenEnvironment] }, signal: AbortSignal.timeout(15000) });
  assert.ok(response.ok, route + ':' + response.status); return response.json();
}
try {
  const installed = (await read('extensions')).items.filter(item => item.isInstalled);
  const catalog = await read('extensions/catalog/stable');
  const target = catalog.entries.find(item => item.extensionId === input.context.uninstalledExtensionId);
  assert.ok(target?.metadata && !installed.some(item => item.extensionId === target.extensionId));
  assert.ok(installed.length > 1 && catalog.entries.length > installed.length);
  assert.ok(catalog.entries.every(item => item.metadata?.displayName && item.metadata.locales['en-US']?.displayName));
  assert.ok(installed.every(item => item.metadata?.createdAt && item.metadata?.updatedAt));
  browser = await chromium.launch({ executablePath: input.browser, headless: true });
  const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
  page.setDefaultTimeout(20000); page.on('pageerror', error => errors.push(String(error)));
  await page.goto(input.address);
  await page.locator('.authentication').waitFor({ state: 'detached' });
  await page.locator('a[href="#/plugins"]').first().click();
  const rows = page.locator('.plugin-row');
  const search = page.locator('.plugin-search nexus-text-input input');
  await page.waitForFunction(count => document.querySelectorAll('.plugin-row').length === count, installed.length);
  await page.getByRole('button', { name: '插件仓库', exact: true }).click();
  await page.waitForFunction(count => document.querySelectorAll('.plugin-row').length === count, catalog.entries.length);
  checks.push('uninstalled packages display complete localized catalog metadata alongside installed packages');
  for (const query of input.context.searches) {
    await search.fill(query);
    await page.waitForFunction(name => {
      const rows = document.querySelectorAll('.plugin-row');
      return rows.length === 1 && rows[0].querySelector('strong')?.textContent === name;
    }, target.metadata.displayName);
    await rows.first().getByText('未安装', { exact: true }).waitFor({ state: 'visible' });
  }
  checks.push('uninstalled package matches Chinese, English, full pinyin and initials');
  await page.screenshot({ path: path.join(input.output, 'uninstalled-search.png') });
  await page.getByRole('button', { name: '本地插件', exact: true }).click();
  assert.equal(await search.inputValue(), '');
  await page.waitForFunction(count => document.querySelectorAll('.plugin-row').length === count, installed.length);
  await search.fill(installed[0].artifactName);
  await page.waitForFunction(name => document.querySelectorAll('.plugin-row').length === 1 &&
    document.querySelector('.plugin-row strong')?.textContent === name, installed[0].metadata.displayName);
  await page.getByRole('button', { name: '插件仓库', exact: true }).click();
  assert.equal(await search.inputValue(), input.context.searches.at(-1));
  await page.getByRole('button', { name: '本地插件', exact: true }).click();
  assert.equal(await search.inputValue(), installed[0].artifactName);
  checks.push('installed and store views preserve independent search state');
  await search.fill('');
  await page.getByRole('button', { name: '筛选', exact: true }).click();
  const filters = page.getByRole('dialog', { name: '插件筛选与排序', exact: true });
  await filters.getByRole('button', { name: '更新时间', exact: true }).click();
  for (const [label, direction] of [['正序', 1], ['逆序', -1]]) {
    await filters.getByRole('button', { name: label, exact: true }).click();
    const expected = [...installed].sort((a, b) => direction * (Date.parse(a.metadata.updatedAt) - Date.parse(b.metadata.updatedAt)))
      .map(item => item.metadata.displayName);
    await page.waitForFunction(names => JSON.stringify(Array.from(document.querySelectorAll('.plugin-row strong'),
      item => item.textContent)) === JSON.stringify(names), expected);
  }
  await page.keyboard.press('Escape');
  checks.push('installed date ordering uses successful installation facts in both directions');
  await page.locator('a[href="#/settings"]').first().click();
  const language = page.getByRole('combobox', { name: '界面语言', exact: true });
  await language.click();
  await page.getByRole('option', { name: 'English', exact: true }).click();
  await page.locator('a[href="#/plugins"]').first().click();
  await page.getByRole('button', { name: 'Plugin repository', exact: true }).click();
  await page.waitForFunction(name => document.querySelector('.plugin-row strong')?.textContent === name,
    target.metadata.locales['en-US'].displayName);
  await rows.first().getByText('Not installed', { exact: true }).waitFor({ state: 'visible' });
  assert.equal(await rows.first().locator('.row-summary').innerText(), target.metadata.locales['en-US'].summary);
  await page.screenshot({ path: path.join(input.output, 'uninstalled-english.png') });
  checks.push('interface language change immediately localizes the uninstalled package name, summary and state');
  assert.deepEqual(errors, []);
  assert.equal((await read('execution/active')).items.length, 0);
  fs.writeFileSync(path.join(input.output, 'result.json'), JSON.stringify({ status: 'PASS', checks, errors,
    scope: 'Original Test Host, signed local catalog, original packages and actual browser; publication dates and remote trust are separate',
    installed: installed.map(item => ({ extensionId: item.extensionId, metadata: item.metadata })),
    uninstalled: target.extensionId, browserVersion: browser.version() }, null, 2));
} catch (error) {
  fs.writeFileSync(path.join(input.output, 'result.json'), JSON.stringify({ status: 'FAIL', checks, errors, failure: String(error) }, null, 2));
  process.exitCode = 1;
} finally { await browser?.close(); }
