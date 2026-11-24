import assert from 'node:assert/strict';
import { completionLabels, planGroups, planState } from '../../apps/web/src/pages/execution-plan';
import type { AutomationBundle } from '../../apps/web/src/control/wire.generated';
import { locale, t } from '../../apps/web/src/app/locale';
import { executionPreview } from '../../apps/web/src/control/execution';
export async function run(): Promise<string[]> {
  assert.equal(planState([]), 'blocked');
  const cases = ['an empty execution plan is blocked'];
  const identity = executionPreview({ hasRunnable: true, hasBlockedItems: false,
    reasonCode: null, completionAction: 'none', groups: [], items: [
      { ordinal: 0, bindingId: 'binding-a', scriptId: 'script-a', userId: 'user-a',
        disposition: 'runnable', reasonCode: 'execution_inputs_verified' }
    ] }).items[0];
  assert.deepEqual([identity?.ordinal, identity?.bindingId], [0, 'binding-a']);
  cases.push('the preview retains the order and identity of an account binding');
  const previous = locale.value;
  try {
    for (const [action, chinese, english] of [['none', '', ''], ['shutdown', '关机', 'Shut down'],
      ['hibernate', '休眠', 'Hibernate'], ['restart', '重启', 'Restart'], ['exit', '退出程序', 'Exit application']]) {
      const decoded = executionPreview({ hasRunnable: true, hasBlockedItems: false, reasonCode: null,
        completionAction: action, groups: [], items: [] });
      locale.value = 'zh-CN'; assert.equal(t(completionLabels[decoded.completionAction]), chinese);
      locale.value = 'en-US'; assert.equal(t(completionLabels[decoded.completionAction]), english);
      cases.push('completion action is decoded and translated: ' + action);
    }
  } finally { locale.value = previous; }
  const size = 384;
  const scripts = ['first', 'second', 'last'];
  const preview = executionPreview({ hasRunnable: true, hasBlockedItems: false, reasonCode: null, completionAction: 'none',
    groups: Array.from({ length: size }, (_, index) => ({ scriptId: scripts[index % 3], reasonCode: 'execution_inputs_verified' })),
    items: Array.from({ length: size * 2 }, (_, index) => ({ ordinal: size * 2 - 1 - index, bindingId: 'binding-' + index,
      scriptId: scripts[index % 3], userId: 'account-' + index, disposition: 'runnable', reasonCode: 'execution_inputs_verified' })) });
  let reads = 0;
  for (const descriptor of preview.groups) {
    const id = descriptor.scriptId;
    Object.defineProperty(descriptor, 'scriptId', { get: () => { reads++; return id; } });
  }
  const bundle = { scripts: scripts.map(id => ({ id, name: id.toUpperCase() })), queues: [{ id: 'queue', successDependencies: [
    { scriptId: 'last', requiredScriptIds: ['second', 'first', 'missing', 'second'] },
    { scriptId: 'last', requiredScriptIds: ['unselected-later-duplicate'] }
  ] }] } as AutomationBundle;
  const sections = planGroups(preview, bundle, 'queue', 'queue');
  assert.equal(sections.length, size);
  assert.deepEqual(sections.slice(0, 6).map(group => [group.scriptId, group.round, group.members.map(member => member.ordinal)]), [
    ['first', 1, [2, 5]], ['second', 1, [1, 4]], ['last', 1, [0, 3]],
    ['first', 2, [8, 11]], ['second', 2, [7, 10]], ['last', 2, [6, 9]]
  ]);
  assert.deepEqual(sections[5]?.dependencies, [
    { order: 1, name: 'FIRST' }, { order: 2, name: 'SECOND' }, { order: 4, name: 'FIRST' }, { order: 5, name: 'SECOND' }
  ]);
  assert.deepEqual(sections[0]?.dependencies, []);
  assert(reads <= size * 2, `${size} plan groups traverse ${reads} descriptor keys`);
  cases.push(`${size} repeated script groups keep their rounds, accounts, prerequisite order and first declaration (${reads} descriptor reads)`);
  return cases;
}
