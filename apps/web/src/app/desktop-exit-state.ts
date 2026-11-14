import { onBeforeUnmount, onMounted, ref } from 'vue';
import type { ControlClient } from '../control/client';
import { desktop, type ExitState } from './desktop';
import { registerMessages } from './locale';

registerMessages({
  '正在安全退出…': 'Quitting safely…',
  '正在保存，请完成后再退出。': 'A save is still in progress. Try quitting after it finishes.',
  '页面未响应，已取消退出；托盘仍可使用。': 'The page did not respond. Quit was cancelled; the tray remains available.',
  '宿主仍在结束任务，已取消退出，请稍后重试。': 'The Host is still finishing work. Quit was cancelled; try again later.',
  '页面已更换，请重新退出。': 'The page changed. Try quitting again.'
});
const messages: Record<string, string> = {
  desktop_writes_pending: '正在保存，请完成后再退出。', desktop_exit_in_progress: '正在安全退出…',
  desktop_draft_preparation_timeout: '页面未响应，已取消退出；托盘仍可使用。',
  desktop_close_pending: '宿主仍在结束任务，已取消退出，请稍后重试。', desktop_document_changed: '页面已更换，请重新退出。'
};

export function useDesktopExit(client: ControlClient) {
  const message = ref('');
  const failed = ref(false);
  let stop: (() => void) | undefined;
  const update = (state: ExitState) => {
    if (!['preparing', 'authorizing', 'idle'].includes(state.stage)) return;
    client.setExitBlocked(state.stage !== 'idle');
    failed.value = !!state.code;
    message.value = state.stage === 'idle' ? messages[state.code || ''] || state.code || '' : '正在安全退出…';
  };
  onMounted(() => { stop = desktop?.onExitState(update); });
  onBeforeUnmount(() => { stop?.(); client.setExitBlocked(false); });
  return { exitMessage: message, exitFailed: failed };
}
