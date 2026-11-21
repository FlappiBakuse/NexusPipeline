import assert from 'node:assert/strict';
import { completionLabels, planState } from '../../apps/web/src/pages/execution-plan';
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
  return cases;
}
