import assert from 'node:assert/strict';
import { pluginIndex, matchesPlugin, comparePlugins } from '../../apps/web/src/pages/plugin-catalog';
import { extensionMetadata, metadataText, taskTitle } from '../../apps/web/src/control/extension-metadata';
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
  return cases;
}
