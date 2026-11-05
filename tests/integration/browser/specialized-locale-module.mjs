import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const input = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const token = process.env[input.tokenEnvironment];
if (!token || input.context?.cases?.length !== 8) throw Error('eight_specialized_locale_fixtures_required');
const { chromium } = await import(pathToFileURL(path.join(input.modules, 'playwright-core/index.mjs')).href);
const checks = [], facts = [], errors = [], runs = [];
let browser, page;
async function call(route, body, missing = false) {
  const response = await fetch(new URL('/control/v1/' + route, input.address), {
    method: body === undefined ? 'GET' : 'POST', headers: { Authorization: 'Bearer ' + token,
      ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }), signal: AbortSignal.timeout(30000) });
  if (missing && response.status === 404) return undefined;
  assert.equal(response.ok, true, route + ':' + response.status + ':' + (response.ok ? '' : await response.text()));
  return response.json();
}
async function until(action, reason) {
  const deadline = Date.now() + 75000;
  while (Date.now() < deadline) { const result = await action(); if (result) return result; await new Promise(resolve => setTimeout(resolve, 100)); }
  throw Error(reason);
}
function compareTitles(actual, expected) {
  assert.equal(actual.length, expected.length);
  for (const task of expected) {
    const saved = actual.find(item => item.taskId === task.taskId);
    assert.ok(saved, task.taskId);
    assert.equal(saved.title, task.title);
    assert.deepEqual(saved.titleLocales ?? {}, task.titleLocales ?? {});
    assert.equal(saved.isEnabled, task.isEnabled);
    assert.equal(saved.isSelected, task.isSelected);
  }
}
async function selection(sample) {
  const catalog = await call('automation');
  const revision = catalog.revisions.find(item => item.kind === 'binding' && item.id === sample.bindingId).revision;
  const installed = (await call('extensions')).items.find(item => item.extensionId === sample.extensionId);
  return { operationId: crypto.randomUUID(), bindingId: sample.bindingId, expectedBindingRevision: revision,
    extensionId: sample.extensionId, expectedInstalledRevision: installed.installedRevision,
    resources: sample.resources.map(resourceId => ({ resourceId, expectedRevision: 1, relativePath: '' })) };
}
async function remove(extensionId) {
  const item = (await call('extensions')).items.find(row => row.extensionId === extensionId);
  const settings = await call('settings'), operationId = crypto.randomUUID();
  await call('extensions/apply', { format: 'nexus.control.extension-apply', version: 1, operationId,
    command: { action: 'remove', extensionId, expectedInstalledRevision: item.installedRevision,
      expectedSettingsRevision: settings.saved.revision, catalog: null, candidate: null } });
  const operation = await until(async () => {
    const value = await call('extensions/operations/' + operationId); assert.notEqual(value.phase, 'quarantined');
    return value.phase === 'committed' && value;
  }, 'specialized_removal_not_committed');
  assert.equal(operation.outcome, 'removed');
}
async function locale(english) {
  await page.goto(input.address + '#/settings');
  await page.getByRole('combobox', { name: english ? '界面语言' : 'Interface language', exact: true }).click();
  await page.getByRole('option', { name: english ? 'English' : '简体中文', exact: true }).click();
}
async function history(sample, run, language) {
  await page.goto(input.address + '#/history?run=' + run.runId);
  await page.locator('.history-tasks li').first().waitFor();
  const visible = await page.locator('.history-tasks').innerText();
  for (const task of sample.tasks) assert.ok(visible.includes(task.titleLocales?.[language] ?? task.title),
    sample.extensionId + ':' + language + ':' + task.taskId);
  await page.screenshot({ path: path.join(input.output, sample.extensionId + '-' + language + '.png'), animations: 'disabled' });
  const modal = page.locator('nexus-modal').filter({ has: page.locator('.history-detail-content') });
  await modal.locator('button[slot="footer"]').click(); await modal.waitFor({ state: 'hidden' });
}
try {
  browser = await chromium.launch({ executablePath: input.browser, headless: true });
  page = await browser.newPage({ viewport: { width: 1280, height: 900 } }); page.setDefaultTimeout(15000);
  page.on('pageerror', error => errors.push(String(error)));
  await page.goto(input.address);
  for (const sample of input.context.cases) {
    const selected = await selection(sample);
    const preview = await call('adapters/discover', { format: 'nexus.control.adapter-command', version: 1, action: 'discover', selection: selected });
    compareTitles(preview.tasks, sample.tasks);
    assert.deepEqual(preview.diagnostics.map(item => item.code), sample.diagnostics.map(item => item.code));
    assert.ok(!preview.diagnostics.some(item => item.severity === 'blocking'));
    checks.push(sample.extensionId + ': original installed package previews localized titles and preserves user or unknown titles');
    const catalog = await call('automation'), settings = await call('settings'), operationId = crypto.randomUUID();
    const accepted = await call('execution/start/' + operationId, { format: 'nexus.control.execution-selection', version: 1,
      target: { kind: 'binding', bindingId: sample.bindingId }, expectedCatalogRevision: catalog.catalogRevision,
      expectedSettingsRevision: settings.saved.revision });
    const run = { operationId, runId: accepted.runId, attemptId: accepted.attemptId, extensionId: sample.extensionId }; runs.push(run);
    const record = await until(() => call('records/' + run.runId, undefined, true), 'frozen_history_missing:' + sample.extensionId);
    assert.equal(record.finalAttempt.recovery, 'restored');
    const report = await call(`records/${run.runId}/attempts/${record.finalAttempt.attemptId}/report`);
    compareTitles(report.steps.flatMap(step => step.tasks), sample.tasks);
    assert.match(fs.readFileSync(sample.marker, 'utf8'), /^[1-9][0-9]*$/);
    for (const file of sample.files) assert.equal(fs.readFileSync(file.path, 'utf8'), file.content);
    await until(async () => !(await call('execution/active')).items.some(item => item.runId === run.runId),
      'completed_locale_run_still_active:' + sample.extensionId);
    facts.push({ extensionId: sample.extensionId, preview, finalAttempt: record.finalAttempt, report });
    checks.push(sample.extensionId + ': actual controlled process produces frozen history and restores original configuration bytes');
  }
  assert.equal((await call('execution/active')).items.length, 0);
  for (const sample of input.context.cases) await remove(sample.extensionId);
  for (const [index, sample] of input.context.cases.entries()) await history(sample, runs[index], 'zh-CN');
  await locale(true);
  for (const [index, sample] of input.context.cases.entries()) {
    await history(sample, runs[index], 'en-US');
    const record = await call('records/' + runs[index].runId);
    const report = await call(`records/${runs[index].runId}/attempts/${record.finalAttempt.attemptId}/report`);
    compareTitles(report.steps.flatMap(step => step.tasks), sample.tasks);
    checks.push(sample.extensionId + ': removed original package retains both history locales and custom titles in the actual UI');
  }
  assert.equal((await call('execution/active')).items.length, 0); assert.equal((await call('status')).pendingOperations, 0);
  assert.deepEqual(errors, []); checks.push('eight controlled flows and removals finish without active or pending work');
  fs.writeFileSync(path.join(input.output, 'result.json'), JSON.stringify({ status: 'PASS', checks, facts, runs,
    scope: 'Eight original packaged adapters, actual product Host and UI, synthetic configurations and controlled executable; upstream execution NOT_RUN' }, null, 2));
} catch (error) {
  fs.writeFileSync(path.join(input.output, 'result.json'), JSON.stringify({ status: 'FAIL', checks, facts, runs, errors,
    active: await call('execution/active').catch(() => null), serverStatus: await call('status').catch(() => null),
    failure: String(error.stack || error).replaceAll(token, '[redacted]'),
    visibleText: page ? (await page.locator('body').innerText().catch(() => '')).slice(-5000) : '' }, null, 2));
  process.exitCode = 1;
} finally {
  for (const run of runs) {
    if (!(await call('execution/active')).items.some(item => item.runId === run.runId)) continue;
    if (!await call('records/' + run.runId, undefined, true)) await call(`execution/${run.operationId}/cancel/${crypto.randomUUID()}`, {
      format: 'nexus.control.execution-cancellation', version: 1, runId: run.runId, attemptId: run.attemptId });
    await until(async () => !(await call('execution/active')).items.some(item => item.runId === run.runId), 'owned_locale_run_not_stopped');
  }
  await browser?.close();
}
