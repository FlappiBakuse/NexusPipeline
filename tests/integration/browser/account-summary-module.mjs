import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const input = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const token = process.env[input.tokenEnvironment];
if (!token || !input.context?.bindings || !path.isAbsolute(input.browser || '')) throw Error('account_fixture_required');
const { chromium } = await import(pathToFileURL(path.join(input.modules, 'playwright-core/index.mjs')).href);
const checks = [], errors = [], runs = [], records = [];
let browser, page, cancellation;
async function call(route, body) {
  const response = await fetch(new URL('/control/v1/' + route, input.address), {
    method: body === undefined ? 'GET' : 'POST', headers: { Authorization: 'Bearer ' + token,
      ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }), signal: AbortSignal.timeout(30000) });
  assert.equal(response.ok, true, route + ':' + response.status + ':' + (response.ok ? '' : await response.text()));
  return response.json();
}
async function until(action, reason) {
  const deadline = Date.now() + 40000;
  while (Date.now() < deadline) { const value = await action(); if (value) return value; await new Promise(resolve => setTimeout(resolve, 100)); }
  throw Error(reason);
}
const summaries = async () => (await call('automation/users/summary')).items;
async function start(bindingId) {
  const definitions = await call('automation'), settings = await call('settings'), operationId = crypto.randomUUID();
  const accepted = await call('execution/start/' + operationId, { format: 'nexus.control.execution-selection', version: 1,
    target: { kind: 'binding', bindingId }, expectedCatalogRevision: definitions.catalogRevision, expectedSettingsRevision: settings.saved.revision });
  const run = { operationId, runId: accepted.runId, attemptId: accepted.attemptId }; runs.push(run); return run;
}
async function cancel(run) {
  await call(`execution/${run.operationId}/cancel/${crypto.randomUUID()}`, {
    format: 'nexus.control.execution-cancellation', version: 1, runId: run.runId, attemptId: run.attemptId });
  await until(async () => !(await call('execution/active')).items.some(item => item.runId === run.runId), 'owned_task_did_not_stop');
}
async function removeFixture() {
  const item = (await call('extensions')).items.find(item => item.extensionId === 'fixture-plugin');
  const settings = await call('settings'), operationId = crypto.randomUUID();
  await call('extensions/apply', { format: 'nexus.control.extension-apply', version: 1, operationId,
    command: { action: 'remove', extensionId: item.extensionId, expectedInstalledRevision: item.installedRevision,
      expectedSettingsRevision: settings.saved.revision, catalog: null, candidate: null } });
  const result = await until(async () => {
    const state = await call('extensions/operations/' + operationId);
    assert.notEqual(state.phase, 'quarantined'); return state.phase === 'committed' && state;
  }, 'fixture_uninstall_did_not_commit');
  assert.equal(result.outcome, 'removed');
}
try {
  const [empty, success, running, scheduled] = input.context.users;
  for (const [index, tone] of ['bad', 'ok'].entries()) {
    const run = await start(input.context.bindings[index]);
    const summary = await until(async () => (await summaries()).find(item => item.userId === success.userId)
      ?.bindings.find(item => item.bindingId === input.context.bindings[index] && item.runId === run.runId), 'binding_report_missing');
    assert.equal(summary.tone, tone); records.push(summary);
  }
  let summary = (await summaries()).find(item => item.userId === success.userId);
  assert.equal(summary.summaryTone, 'bad'); assert.equal(summary.runId, records[0].runId);
  assert.notEqual(records[0].runId, records[1].runId); assert.ok(Date.parse(records[1].endedAt) > Date.parse(records[0].endedAt));
  checks.push('actual specialized runs publish older failure and newer success; the account selects the older worst binding');
  const waiting = await start(input.context.bindings[2]);
  await until(async () => (await summaries()).find(item => item.userId === running.userId)?.activeCount === 1, 'activity_missing');
  browser = await chromium.launch({ executablePath: input.browser, headless: true });
  page = await browser.newPage({ viewport: { width: 1280, height: 900 } }); page.setDefaultTimeout(20000);
  page.on('pageerror', error => errors.push(String(error)));
  await page.goto(input.address); await page.locator('a[href="#/users"]').first().click();
  const row = user => page.locator(`.automation-row[data-item-id="${user.userId}"]`);
  await row(empty).getByText('专项任务 · 未配置专项任务', { exact: true }).waitFor();
  await row(running).getByText('运行中 · 1', { exact: true }).waitFor();
  const future = (await summaries()).find(item => item.userId === scheduled.userId);
  assert.equal(future.queueId, input.context.queueId); assert.ok(Date.parse(future.nextRunAt) > Date.now());
  await row(scheduled).locator('nexus-badge').filter({ hasText: '下次运行' }).waitFor();
  const badge = row(success).locator('.summary-link nexus-badge'); await badge.waitFor();
  assert.equal(await badge.evaluate(element => element.tone), 'danger');
  await page.waitForFunction(id => {
    const row = document.querySelector(`.automation-row[data-item-id="${id}"]`);
    if (!row?.checkVisibility()) return false;
    for (let element = row; element; element = element.parentElement)
      if (Number(getComputedStyle(element).opacity) < 1) return false;
    return true;
  }, success.userId);
  await page.screenshot({ path: path.join(input.output, 'account-summary.png'), animations: 'disabled' });
  checks.push('actual cards distinguish an empty account, one active specialized report, the worst tone and a future queue countdown');
  for (const activation of ['click', 'Enter', 'Space']) {
    const link = row(success).locator('.summary-link');
    if (activation === 'click') await link.click(); else { await link.focus(); await link.press(activation); }
    await page.waitForURL(url => url.hash === '#/history?run=' + records[0].runId);
    await page.locator('.history-detail-metadata').getByText(success.name, { exact: true }).waitFor();
    await page.locator('.history-detail-content').getByText('受控每日任务', { exact: true }).first().waitFor();
    await page.locator('.history-detail-content').getByText('专项任务失败', { exact: true }).first().waitFor();
    await page.locator('nexus-modal').filter({ has: page.locator('.history-detail-content') }).locator('button[slot="footer"]').click();
    await page.locator('a[href="#/users"]').first().click(); await row(success).locator('.summary-link').waitFor();
  }
  checks.push('mouse, Enter and Space all open the same older failure record and its frozen task report in the actual history view');
  await cancel(waiting); waiting.stopped = true;
  cancellation = (await call('records/' + waiting.runId)).finalAttempt;
  assert.equal(cancellation.lifecycle, 'cancelled'); assert.equal(cancellation.recovery, 'restored');
  await until(async () => (await summaries()).find(item => item.userId === running.userId)?.activeCount === 0, 'activity_not_cleared');
  await page.locator('a[href="#/settings"]').first().click();
  await page.getByRole('combobox', { name: '界面语言', exact: true }).click();
  await page.getByRole('option', { name: 'English', exact: true }).click();
  await removeFixture();
  await page.goto(input.address + '#/history?run=' + records[0].runId);
  await page.locator('.history-detail-content').getByText('Controlled daily task', { exact: true }).first().waitFor();
  await page.locator('.history-detail-content').getByText('Specialized task failed', { exact: true }).first().waitFor();
  await page.screenshot({ path: path.join(input.output, 'history-after-uninstall.png') });
  const original = await call('records/' + records[0].runId);
  assert.equal(original.finalAttempt.runId, records[0].runId);
  checks.push('completed history retains its frozen English task title after the contributing adapter is uninstalled');
  assert.equal((await call('execution/active')).items.length, 0); assert.equal((await call('status')).pendingOperations, 0);
  assert.deepEqual(errors, []);
  checks.push('the real waiting task is cancelled and restored; no active or pending operation remains');
  fs.writeFileSync(path.join(input.output, 'result.json'), JSON.stringify({ status: 'PASS', checks, records, runs, cancellation,
    scope: 'Current full product Host and production UI with an installed controlled pure-data task adapter and actual owned executables; no SQL or browser-response seeding' }, null, 2));
} catch (error) {
  fs.writeFileSync(path.join(input.output, 'result.json'), JSON.stringify({ status: 'FAIL', checks, errors, records, runs, cancellation,
    failure: String(error.stack || error).replaceAll(token, '[redacted]'),
    visibleText: page ? (await page.locator('body').innerText().catch(() => '')).slice(-5000) : '' }, null, 2));
  process.exitCode = 1;
} finally {
  for (const run of runs.filter(item => !item.stopped)) {
    const active = (await call('execution/active').catch(() => ({ items: [] }))).items;
    if (active.some(item => item.runId === run.runId)) await cancel(run);
  }
  await browser?.close();
}
