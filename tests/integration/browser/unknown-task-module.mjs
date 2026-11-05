import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const input = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const token = process.env[input.tokenEnvironment];
if (!token || input.context?.cases?.length !== 2) throw Error('unknown_task_fixture_required');
const { chromium } = await import(pathToFileURL(path.join(input.modules, 'playwright-core/index.mjs')).href);
const checks = [], errors = [], runs = [], facts = [], reads = [];
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
  const deadline = Date.now() + 70000;
  while (Date.now() < deadline) { const value = await action(); if (value) return value; await new Promise(resolve => setTimeout(resolve, 100)); }
  throw Error(reason);
}
async function start(bindingId) {
  const catalog = await call('automation'), settings = await call('settings'), operationId = crypto.randomUUID();
  const accepted = await call('execution/start/' + operationId, { format: 'nexus.control.execution-selection', version: 1,
    target: { kind: 'binding', bindingId }, expectedCatalogRevision: catalog.catalogRevision, expectedSettingsRevision: settings.saved.revision });
  const run = { operationId, runId: accepted.runId, attemptId: accepted.attemptId }; runs.push(run); return run;
}
async function discover(bindingId) {
  const catalog = await call('automation'), binding = catalog.bundle.bindings.find(item => item.id === bindingId);
  const revision = catalog.revisions.find(item => item.kind === 'binding' && item.id === bindingId).revision;
  assert.ok(binding);
  const installed = (await call('extensions')).items.find(item => item.extensionId === 'baah');
  return call('adapters/discover', { format: 'nexus.control.adapter-command', version: 1, action: 'discover', selection: {
    operationId: crypto.randomUUID(), bindingId, expectedBindingRevision: revision, extensionId: 'baah',
    expectedInstalledRevision: installed.installedRevision,
    resources: ['main', 'software-config'].map(resourceId => ({ resourceId, expectedRevision: 1, relativePath: '' })) } });
}
async function removePackage() {
  const item = (await call('extensions')).items.find(item => item.extensionId === 'baah');
  const settings = await call('settings'), operationId = crypto.randomUUID();
  await call('extensions/apply', { format: 'nexus.control.extension-apply', version: 1, operationId,
    command: { action: 'remove', extensionId: 'baah', expectedInstalledRevision: item.installedRevision,
      expectedSettingsRevision: settings.saved.revision, catalog: null, candidate: null } });
  const result = await until(async () => {
    const value = await call('extensions/operations/' + operationId); assert.notEqual(value.phase, 'quarantined');
    return value.phase === 'committed' && value;
  }, 'package_removal_not_committed');
  assert.equal(result.outcome, 'removed');
}
const closeHistory = async () => {
  const modal = page.locator('nexus-modal').filter({ has: page.locator('.history-detail-content') });
  await modal.locator('button[slot="footer"]').click(); await modal.waitFor({ state: 'hidden' });
};
try {
  browser = await chromium.launch({ executablePath: input.browser, headless: true });
  page = await browser.newPage({ viewport: { width: 1280, height: 900 } }); page.setDefaultTimeout(20000);
  page.on('pageerror', error => errors.push(String(error)));
  page.on('response', async response => {
    if (new URL(response.url()).pathname !== '/control/v1/execution/active') return;
    try { reads.push({ status: response.status(), body: await response.json() }); if (reads.length > 4) reads.shift(); } catch {}
  });
  await page.goto(input.address); await page.locator('a[href="#/dispatch"]').first().click();
  for (const sample of input.context.cases) {
    const preview = await discover(sample.bindingId);
    const unknown = preview.tasks.find(task => task.title === sample.unknownTitle);
    assert.ok(unknown); assert.equal(unknown.isEnabled, sample.unknownEnabled); assert.equal(unknown.isSelected, false);
    assert.equal(unknown.isRetrySafe, false); assert.deepEqual(unknown.titleLocales, {});
    assert.ok(preview.tasks.some(task => task.title === '咖啡馆' && task.titleLocales['en-US'] === 'Cafe'));
    assert.equal(preview.tasks.find(task => task.taskId === 'baah:automatic-login').titleLocales['en-US'], 'Automatic login');
    assert.equal(preview.tasks.find(task => task.taskId === 'baah:automatic-cleanup').titleLocales['en-US'], 'Cleanup');
    assert.ok(!preview.diagnostics.some(item => item.severity === 'blocking'));
    const business = preview.tasks.filter(task => task.isSelected && task.workflowRole === 'business');
    assert.equal(business.length, 2);
    if (sample.unknownEnabled) assert.ok(business.every(task => task.isRetrySafe === false));
    checks.push('original BAAH discovery preserves unknown ' + sample.unknownEnabled + ' outside selected business and retry safety');
    const run = await start(sample.bindingId);
    const observed = await until(async () => (await call('execution/active')).items.find(item => item.runId === run.runId), 'accepted_run_never_active');
    facts.push({ runId: run.runId, initialActive: observed });
    const card = page.locator(`.running-item[data-run-id="${run.runId}"]`);
    await card.waitFor();
    const unknownCard = card.locator(`.live-task-steps [data-task-id=${JSON.stringify(unknown.taskId)}]`);
    if (sample.unknownEnabled) {
      await unknownCard.waitFor();
      assert.equal(await unknownCard.locator('.task-state').evaluate(element => element.label), '未核验');
      fs.writeFileSync(sample.log + '.release', 'release controlled task');
    }
    else assert.equal(await unknownCard.count(), 0);
    checks.push('actual running card does not claim that enabled unselected upstream tasks were never executed: ' + sample.unknownEnabled);
    const record = await until(async () => call('records/' + run.runId, undefined, true), 'completed_record_missing');
    assert.equal(record.finalAttempt.business, 'succeeded'); assert.equal(record.finalAttempt.recovery, 'restored');
    const report = await call(`records/${run.runId}/attempts/${record.finalAttempt.attemptId}/report`);
    const task = report.steps.flatMap(step => step.tasks).find(task => task.taskId === unknown.taskId);
    assert.equal(task.isSelected, false); assert.equal(task.isEnabled, sample.unknownEnabled); assert.equal(task.state, 'unknown');
    assert.equal(task.completionKind ?? null, null);
    const attempts = fs.readFileSync(sample.log + '.attempts', 'utf8').trim().split('\n').map(line => JSON.parse(line));
    assert.equal(attempts.length, sample.expectedCalls);
    assert.ok(attempts.every(item => item.flags[3] === sample.unknownEnabled));
    if (!sample.unknownEnabled) assert.deepEqual(attempts[1].flags, [true, false, true, false]);
    assert.equal(fs.readFileSync(sample.configuration, 'utf8'), sample.original);
    assert.equal(fs.readFileSync(sample.software, 'utf8'), sample.originalSoftware);
    facts.push({ runId: run.runId, unknown: task, attempts, finalAttempt: record.finalAttempt });
    await page.goto(input.address + '#/history?run=' + run.runId);
    const row = page.locator('.history-tasks li').filter({ hasText: sample.unknownTitle });
    await row.waitFor(); await row.getByText(sample.unknownEnabled ? '待核实' : '已停用', { exact: true }).waitFor();
    await page.screenshot({ path: path.join(input.output, 'unknown-' + sample.unknownEnabled + '-zh.png'), animations: 'disabled' });
    await closeHistory(); await page.locator('a[href="#/dispatch"]').first().click();
    checks.push('actual execution and safe retry retain unknown switch, nested original bytes and restored configuration: ' + sample.unknownEnabled);
  }
  await removePackage();
  await page.locator('a[href="#/settings"]').first().click();
  await page.getByRole('combobox', { name: '界面语言', exact: true }).click();
  await page.getByRole('option', { name: 'English', exact: true }).click();
  for (const [index, sample] of input.context.cases.entries()) {
    await page.goto(input.address + '#/history?run=' + runs[index].runId);
    await page.locator('.history-tasks li').getByText('Cafe', { exact: true }).waitFor();
    await page.locator('.history-tasks li').getByText('Automatic login', { exact: true }).waitFor();
    await page.locator('.history-tasks li').getByText('Cleanup', { exact: true }).waitFor();
    const row = page.locator('.history-tasks li').filter({ hasText: sample.unknownTitle });
    await row.getByText(sample.unknownEnabled ? 'Unverified' : 'Disabled', { exact: true }).waitFor();
    await page.screenshot({ path: path.join(input.output, 'unknown-' + sample.unknownEnabled + '-en.png'), animations: 'disabled' });
    await closeHistory();
    checks.push('uninstalled BAAH history retains frozen English known titles and original user unknown title: ' + sample.unknownEnabled);
  }
  assert.equal((await call('execution/active')).items.length, 0); assert.equal((await call('status')).pendingOperations, 0);
  assert.deepEqual(errors, []); checks.push('both real process flows complete with restored configuration and no active or pending work');
  fs.writeFileSync(path.join(input.output, 'result.json'), JSON.stringify({ status: 'PASS', checks, facts, runs,
    scope: 'Original BAAH package and current product Host/UI; controlled upstream executable with actual file logs and safe retry; no game or account execution' }, null, 2));
} catch (error) {
  fs.writeFileSync(path.join(input.output, 'result.json'), JSON.stringify({ status: 'FAIL', checks, errors, facts, runs, reads,
    failure: String(error.stack || error).replaceAll(token, '[redacted]'),
    visibleText: page ? (await page.locator('body').innerText().catch(() => '')).slice(-6000) : '' }, null, 2));
  process.exitCode = 1;
} finally {
  for (const run of runs) {
    const active = (await call('execution/active').catch(() => ({ items: [] }))).items;
    if (active.some(item => item.runId === run.runId)) {
      if (!await call('records/' + run.runId, undefined, true)) {
        try {
          await call(`execution/${run.operationId}/cancel/${crypto.randomUUID()}`, {
            format: 'nexus.control.execution-cancellation', version: 1, runId: run.runId, attemptId: run.attemptId });
        } catch (error) {
          if (!await call('records/' + run.runId, undefined, true)) throw error;
        }
      }
      await until(async () => !(await call('execution/active')).items.some(item => item.runId === run.runId), 'owned_task_did_not_stop');
    }
  }
  await browser?.close();
}
