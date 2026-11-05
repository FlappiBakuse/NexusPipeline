import assert from 'node:assert/strict';
import { pluginIndex, matchesPlugin, comparePlugins } from '../../apps/web/src/pages/plugin-list';
import { extensionMetadata, metadataText, taskTitle } from '../../apps/web/src/control/extension-metadata';
export async function run(): Promise<string[]> {
  const row = { extensionId: 'sample', name: '截图工具', kind: 'managed-code', summary: '' };
  assert(matchesPlugin(pluginIndex(row), 'j-q-l') === false);
  assert(matchesPlugin(pluginIndex(row), 'J-T-G-J'));
  assert(matchesPlugin(pluginIndex(row), 'jie tu gong ju'));
  assert.equal(comparePlugins(row, row, 'updated', true), 0);
  const localized = extensionMetadata({ defaultLocale: 'zh-CN', displayName: '绝区零', gameName: '绝区零', summary: '任务',
    author: 'Example Author', tags: ['automation'], createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-02-01T00:00:00Z',
    locales: { 'zh-CN': { displayName: '绝区零', gameName: '绝区零', summary: '任务' },
      'en-US': { displayName: 'Zenless Zone Zero', gameName: 'Zenless', summary: 'Tasks' } } })!;
  const sample = { extensionId: 'zzz', name: '绝区零', summary: '', kind: 'data-specialized', metadata: localized };
  const index = pluginIndex(sample), cases = ['localized names support normalized full pinyin and initials'];
  for (const query of ['绝区零', 'juequling', 'JQL', 'jue qu ling', 'jue-qu-ling', 'ZENLESS', 'ＺＥＮＬＥＳＳ', 'Example Author', 'automation']) {
    assert(matchesPlugin(index, query), query); cases.push('single-source metadata search: ' + query);
  }
  assert.equal(metadataText(localized, 'en-US').displayName, 'Zenless Zone Zero');
  assert.equal(metadataText(localized, 'unsupported').displayName, '绝区零');
  assert.equal(taskTitle({ title: '用户自定义名' }, 'en-US'), '用户自定义名');
  const missing = { ...sample, extensionId: 'missing', metadata: { ...localized, createdAt: null, updatedAt: null } };
  const older = { ...sample, extensionId: 'older', metadata: { ...localized, updatedAt: '2025-01-01T00:00:00Z' } };
  for (const descending of [true, false]) {
    assert.equal([missing, sample, older].sort((a, b) => comparePlugins(a, b, 'updated', descending)).at(-1)!.extensionId, 'missing');
    const sameDate = [sample, { ...sample, extensionId: 'aaa' }].sort((a, b) => comparePlugins(a, b, 'updated', descending));
    assert.equal(sameDate[0]!.extensionId, 'aaa');
  }
  assert.equal([sample, older].sort((a, b) => comparePlugins(a, b, 'updated', true))[0]!.extensionId, 'zzz');
  assert.equal([sample, older].sort((a, b) => comparePlugins(a, b, 'updated', false))[0]!.extensionId, 'older');
  cases.push('date order keeps missing values last and ties stable in both directions');
  const changed = { ...sample, metadata: { ...localized, displayName: '新名称', gameName: '新游戏', locales: {} } };
  assert(matchesPlugin(pluginIndex(changed), 'xin ming cheng')); assert(!matchesPlugin(pluginIndex(changed), 'Zenless'));
  cases.push('metadata replacement rebuilds the actual search index');
  return cases;
}
