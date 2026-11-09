import assert from 'node:assert/strict';

function faults(page) {
  const ledger = { writes: [], queries: [], committedResponseLost: false };
  let phase = 'lost-write';
  const pattern = '**/control/v1/extensions/game-checkin/frontend/*/invoke/*';
  const handler = async route => {
    const operation = new URL(route.request().url()).pathname.split('/').at(-1);
    const body = route.request().postDataJSON();
    if (operation === 'save-task') {
      ledger.writes.push({ operationId: body.operationId, credentialAction: body.input.secrets.cn.action });
      if (phase === 'lost-write') {
        const response = await route.fetch();
        const reply = await response.json();
        ledger.committedResponseLost = response.ok() && reply.ok === true;
        await route.abort('connectionreset'); return;
      }
    }
    if (operation === 'read-publication') {
      ledger.queries.push({ operationId: body.input.publicationOperationId, writeIdentity: body.operationId ?? null });
      if (phase === 'offline') { await route.abort('connectionreset'); return; }
      if (phase === 'not-observed') {
        const response = await route.fetch();
        const reply = await response.json();
        assert.equal(reply.ok, true);
        await route.fulfill({ response, json: { ...reply, value: { committed: false } } }); return;
      }
    }
    if (operation === 'read-state' && phase === 'refresh-fails') {
      await route.abort('connectionreset'); return;
    }
    await route.continue();
  };
  return { ledger, setPhase: value => { phase = value; },
    install: () => page.route(pattern, handler), release: () => page.unroute(pattern, handler) };
}

async function reconcile(page, platform, state, fault, checks) {
  const original = (await state()).tasks[0];
  await page.locator('.checkin-task').getByRole('button', { name: '编辑签到', exact: true }).click();
  const editor = page.locator('.checkin-editor');
  await editor.getByRole('textbox', { name: '任务名称', exact: true }).fill('Publication retained draft');
  const secret = editor.locator(`nexus-text-input[aria-label=${JSON.stringify(platform.name + ' 凭据')}] input`);
  await secret.fill('account_id=10001; cookie_token=nexus-controlled-fixture');
  const save = editor.getByRole('button', { name: '保存', exact: true });
  const check = editor.getByRole('button', { name: '检查保存结果', exact: true });
  await save.click(); await check.waitFor();
  assert.equal(fault.ledger.committedResponseLost, true);
  assert.equal(await save.isDisabled(), true);
  assert.equal(await secret.inputValue(), '');
  assert.equal(await editor.getByRole('textbox', { name: '任务名称', exact: true }).inputValue(), 'Publication retained draft');
  checks.push('lost committed save response freezes the original write and clears submitted credential plaintext');
  fault.setPhase('offline');
  await check.click();
  await editor.getByText('保存结果仍待确认，请稍后检查。', { exact: true }).waitFor();
  fault.setPhase('not-observed');
  await check.click();
  await editor.getByText('保存结果仍待确认，请保留草稿并稍后检查。', { exact: true }).waitFor();
  assert.equal(await save.isDisabled(), true);
  fault.setPhase('refresh-fails');
  await check.click();
  await editor.getByText('已提交，刷新失败，请再次检查。', { exact: true }).waitFor();
  const queryCount = fault.ledger.queries.length;
  fault.setPhase('recovered');
  await check.click(); await editor.waitFor({ state: 'detached' });
  assert.equal(queryCount, 3);
  assert.equal(fault.ledger.queries.length, queryCount);
  assert.equal(fault.ledger.writes.length, 1);
  assert.ok(fault.ledger.writes[0].operationId);
  assert.ok(fault.ledger.queries.every(item => item.operationId === fault.ledger.writes[0].operationId && item.writeIdentity === null));
  const after = await state();
  assert.equal(after.tasks.length, 1); assert.equal(after.tasks[0].id, original.id);
  assert.equal(after.tasks[0].name, 'Publication retained draft');
  assert.equal(after.tasks[0].enabled, false);
  checks.push('offline and unobserved publication reads retain identity; committed refresh retries only reads and never saves twice');
  return original.name;
}

async function credentialIntents(page, platform, originalName, fault, checks) {
  const editor = page.locator('.checkin-editor');
  const open = () => page.locator('.checkin-task').getByRole('button', { name: '编辑签到', exact: true }).click();
  const clear = editor.getByRole('button', { name: platform.name + ' · 清除已保存凭据', exact: true });
  const secret = editor.locator(`nexus-text-input[aria-label=${JSON.stringify(platform.name + ' 凭据')}] input`);
  await open(); await clear.waitFor();
  assert.equal(await secret.inputValue(), '');
  await editor.getByRole('textbox', { name: '任务名称', exact: true }).fill('Publication keep');
  await editor.getByRole('button', { name: '保存', exact: true }).click();
  await editor.waitFor({ state: 'detached' });
  await open(); await clear.waitFor();
  assert.equal(await secret.inputValue(), '');
  await clear.click();
  await editor.getByRole('textbox', { name: '任务名称', exact: true }).fill(originalName);
  await editor.getByRole('button', { name: '保存', exact: true }).click();
  await editor.waitFor({ state: 'detached' });
  await open();
  await editor.getByRole('textbox', { name: '任务名称', exact: true }).waitFor();
  assert.equal(await clear.count(), 0); assert.equal(await secret.inputValue(), '');
  await editor.getByRole('button', { name: '取消', exact: true }).click();
  await editor.waitFor({ state: 'detached' });
  assert.deepEqual(fault.ledger.writes.map(item => item.credentialAction), ['set', 'keep', 'clear']);
  assert.equal(new Set(fault.ledger.writes.map(item => item.operationId)).size, 3);
  checks.push('actual credential set, ordinary-edit keep and explicit clear persist without revealing or replaying plaintext');
}

async function translatedRows(page, checks) {
  await page.locator('a[href="#/settings"]').first().click();
  await page.getByRole('combobox', { name: '界面语言', exact: true }).click();
  await page.getByRole('option', { name: 'English', exact: true }).click();
  await page.locator('a[href$="/plugin/game-checkin/tasks"]').first().click();
  await page.locator('.checkin-task').getByText('Disabled', { exact: true }).waitFor();
  await page.locator('.checkin-task').getByRole('button', { name: 'Edit check-in', exact: true }).click();
  const editor = page.locator('.checkin-editor');
  for (const platform of ['Miyoushe', 'HoYoLAB', 'Skland', 'SKPORT', 'Kuro community', 'Tajiduo'])
    await editor.getByRole('combobox', { name: platform + ' Check-in games', exact: true }).waitFor();
  await editor.getByRole('button', { name: 'Cancel', exact: true }).click();
  await editor.waitFor({ state: 'detached' });
  await page.locator('a[href="#/settings"]').first().click();
  await page.getByRole('combobox', { name: 'Interface language', exact: true }).click();
  await page.getByRole('option', { name: '简体中文', exact: true }).click();
  await page.locator('a[href$="/plugin/game-checkin/tasks"]').first().click();
  await page.locator('.checkin-task').getByText('已停用', { exact: true }).waitFor();
  checks.push('current rows and all six platform choices follow interface language');
}

export async function qualifyCheckinRecovery({ page, state, checks }) {
  const platform = (await state()).platforms.find(item => item.id === 'cn');
  const fault = faults(page);
  await fault.install();
  try {
    const originalName = await reconcile(page, platform, state, fault, checks);
    await credentialIntents(page, platform, originalName, fault, checks);
    await translatedRows(page, checks);
    return fault.ledger;
  } finally { await fault.release(); }
}
