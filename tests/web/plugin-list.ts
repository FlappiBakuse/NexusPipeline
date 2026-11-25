import assert from 'node:assert/strict';
import { pluginIndex, matchesPlugin, comparePlugins } from '../../apps/web/src/pages/plugin-catalog';
import { extensionMetadata, metadataText, taskTitle } from '../../apps/web/src/control/extension-metadata';
import { createLocalPlugins } from '../../apps/web/src/pages/local-plugins';
import { ControlFailure } from '../../apps/web/src/control/client';
import type { AppStore } from '../../apps/web/src/app/store';
export async function run(): Promise<string[]> {
  const row = { extensionId: 'sample', name: '截图工具', kind: 'managed-code', summary: '' };
  const fallback = pluginIndex(row);
  assert(matchesPlugin(fallback, 'jie tu gong ju'));
  assert(!matchesPlugin(fallback, 'j-q-l'));
  const localized = extensionMetadata({ defaultLocale: 'zh-CN', displayName: '绝区零', gameName: '绝区零', summary: '任务',
    author: 'Example Author', tags: ['automation'], createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-02-01T00:00:00Z',
    locales: { 'zh-CN': { displayName: '绝区零', gameName: '绝区零', summary: '任务' },
      'en-US': { displayName: 'Zenless Zone Zero', gameName: 'Zenless', summary: 'Tasks' } } })!;
  const sample = { extensionId: 'zzz', name: '绝区零', summary: '', kind: 'data-specialized', metadata: localized };
  const index = pluginIndex(sample), cases = ['search uses the row name when metadata is missing'];
  for (const query of ['绝区零', 'juequling', 'JQL', 'jue qu ling', 'jue-qu-ling', 'ZENLESS', 'ＺＥＮＬＥＳＳ', 'Example Author', 'automation']) {
    assert(matchesPlugin(index, query), query); cases.push('single-source metadata search: ' + query);
  }
  assert.equal(metadataText(localized, 'en-US').displayName, 'Zenless Zone Zero');
  assert.equal(metadataText(localized, 'unsupported').displayName, '绝区零');
  assert.equal(taskTitle({ title: '用户自定义名' }, 'en-US'), '用户自定义名');
  const missing = { ...sample, extensionId: 'missing', metadata: { ...localized, createdAt: null, updatedAt: null } };
  const older = { ...sample, extensionId: 'older', metadata: { ...localized, updatedAt: '2025-01-01T00:00:00Z' } };
  const sameDate = { ...sample, extensionId: 'aaa' };
  for (const descending of [true, false]) {
    const ordered = [missing, sample, older, sameDate].sort((a, b) => comparePlugins(a, b, 'updated', descending));
    assert.deepEqual(ordered.map(row => row.extensionId),
      descending ? ['aaa', 'zzz', 'older', 'missing'] : ['older', 'aaa', 'zzz', 'missing']);
  }
  cases.push('date order keeps missing dates last and breaks ties by ID in both directions');
  const changed = { ...sample, metadata: { ...localized, displayName: '新名称', gameName: '新游戏', locales: {} } };
  const changedIndex = pluginIndex(changed);
  assert(matchesPlugin(changedIndex, 'xin ming cheng')); assert(!matchesPlugin(changedIndex, 'Zenless'));
  cases.push('search follows replacement metadata');
  const names = Array.from({ length: 128 }, (_, index) => ({ ...row, extensionId: 'sort-' + index, name: '脚本 ' + (128 - index) }));
  let collators = 0;
  const original = Intl.Collator;
  try {
    Intl.Collator = new Proxy(original, { construct(target, args, receiver) {
      collators++;
      return Reflect.construct(target, args, receiver);
    } });
    names.sort((left, right) => comparePlugins(left, right, 'name', false));
  } finally { Intl.Collator = original; }
  assert.deepEqual(names.map(item => item.name), Array.from({ length: 128 }, (_, index) => '脚本 ' + (index + 1)));
  assert.equal(collators, 0, `sorting 128 localized names constructs ${collators} collators`);
  cases.push('name ordering reuses one pinyin collator while preserving numeric order for 128 localized names');
  const candidate = { candidateId: 'a'.repeat(64), discoveryId: 'a'.repeat(64), extensionId: 'local-fixture-plugin', name: 'Local plugin',
    summary: 'Local test package', kind: 'managed-code', packageVersion: '1.0.0', minHostVersion: '1.0.0',
    relativePath: 'fixture.zip', capabilities: [], state: 'pendingApproval', reason: null };
  const snapshot = { items: [candidate], scanning: false,
    notice: { batchId: 'startup', removed: ['Removed plugin'], waiting: [], installed: [] } };
  let response = snapshot, remote = false, scans = 0;
  const authoritySignal = new AbortController().signal;
  const store = {
    state: { capabilities: { instanceId: 'fixture' } }, can: () => true,
    client: { authoritySignal, read: async (_path: string, parse: (value: unknown) => unknown) => {
      if (remote) throw new ControlFailure(403, 'control_capability_denied');
      return parse(response);
    }, write: async (_path: string, _body: unknown, parse: (value: unknown) => unknown) => {
      scans++;
      return parse(response);
    } }
  } as unknown as AppStore;
  const local = createLocalPlugins(store);
  await local.refresh();
  assert.equal(local.available.value, true);
  assert.equal(local.items.value[0]?.candidateId, candidate.candidateId);
  assert.equal(local.toast.value?.kind, 'removed');
  local.closeToast();
  assert.equal(local.toast.value?.kind, 'discovered');
  local.closeToast();
  await local.refresh();
  await local.scan();
  assert.equal(scans, 1);
  assert.equal(local.toast.value, undefined);
  response = { ...snapshot, items: [{ ...candidate, candidateId: 'b'.repeat(64) }] };
  await local.scan();
  assert.equal(local.toast.value, undefined);
  response = { ...snapshot, items: [{ ...candidate, candidateId: 'c'.repeat(64), discoveryId: 'c'.repeat(64) }] };
  await local.scan();
  assert.equal(local.toast.value?.kind, 'discovered');
  remote = true;
  await local.refresh();
  await local.scan();
  assert.equal(local.available.value, false);
  assert.equal(local.items.value.length, 0);
  assert.equal(local.toast.value, undefined);
  assert.equal(scans, 3);
  local.dispose();
  cases.push('local discovery orders startup removal before approval notices, deduplicates digests and honors remote denial');
  return cases;
}
