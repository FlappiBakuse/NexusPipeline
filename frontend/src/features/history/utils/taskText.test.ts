import { describe, expect, it } from 'vitest';
import { resolveTaskText } from './taskText';
import type { TaskDisplaySnapshot, TaskTextRef } from './taskTypes';

describe('frozen task text', () => {
  const ref: TaskTextRef = { kind: 'plugin', key: 'custom.name', args: { count: 2 }, fallback: 'Fallback {count}' };
  const snapshot: TaskDisplaySnapshot = { pluginId: 'arbitrary-author', pluginVersion: '1', localizationHash: 'hash', defaultLocale: 'zh-CN',
    messages: { 'en-US': { 'custom.name': 'Reward {count}' }, 'zh-CN': { 'custom.name': '奖励 {count}' } } };
  it('uses exact, language, default and literal fallbacks independently of task IDs', () => {
    expect(resolveTaskText(ref, snapshot, 'en_GB', 'raw')).toBe('Reward 2');
    expect(resolveTaskText(ref, snapshot, 'fr', 'raw')).toBe('奖励 2');
    expect(resolveTaskText(ref, undefined, 'en', 'raw')).toBe('Fallback 2');
    expect(resolveTaskText(undefined, snapshot, 'en', '用户名称')).toBe('用户名称');
    expect(resolveTaskText({ kind: 'literal', value: '<b>literal</b>' }, snapshot, 'en', 'raw')).toBe('<b>literal</b>');
  });
  it('does not recursively interpolate arguments or accept missing placeholders', () => {
    expect(resolveTaskText({ ...ref, args: { count: '{token}' } }, snapshot, 'en', 'raw')).toBe('Reward {token}');
    expect(resolveTaskText({ ...ref, args: {} }, snapshot, 'en', 'raw')).toBe('raw');
  });
});
