import { computed, onBeforeUnmount, ref, watch, type Ref } from 'vue';
import type { AutomationStore } from '../app/store-ports';
import { objectValue } from '../control/client';
import { t } from '../app/locale';
import { scriptRootValid } from './script-paths';

export function useScriptRoot(store: AutomationStore, identity: () => string, path: () => string, field: Ref<HTMLElement | undefined>) {
  const state = ref<'empty' | 'checking' | 'ready' | 'invalid' | 'unavailable'>('empty');
  let owner: AbortController | undefined, timer: ReturnType<typeof setTimeout> | undefined;
  const message = computed(() => state.value === 'ready' || state.value === 'empty' ? '' : t({
    checking: '正在核验 Host 上的脚本根目录。', invalid: '请选择 Host 上实际存在且可读取的完整脚本目录。',
    unavailable: '脚本根目录核验失败，请修改目录后重试。'
  }[state.value]));
  watch(() => [store.state.capabilities?.instanceId, identity(), path(), store.can('automation.read')], () => {
    owner?.abort(); clearTimeout(timer);
    const rootPath = path().trim();
    if (!rootPath) { state.value = 'empty'; return; }
    if (!scriptRootValid(rootPath)) { state.value = 'invalid'; return; }
    if (!store.can('automation.read')) { state.value = 'unavailable'; return; }
    const operation = new AbortController(); owner = operation;
    state.value = 'checking';
    timer = setTimeout(() => {
      void store.client.query('/control/v1/automation/scripts/root', {
        format: 'nexus.control.script-root', version: 1, rootPath
      }, value => {
        const result = objectValue(value);
        if (result.rootPath !== rootPath || typeof result.isDirectory !== 'boolean') throw Error('script_root_response_invalid');
        return result.isDirectory;
      }, operation.signal).then(exists => {
        if (!operation.signal.aborted) state.value = exists ? 'ready' : 'invalid';
      }).catch(() => { if (!operation.signal.aborted) state.value = 'unavailable'; });
    }, 200);
  }, { immediate: true, flush: 'sync' });
  watch([field, message, state], ([element, text, status]) => {
    const input = element?.shadowRoot?.querySelector('input');
    input?.setCustomValidity(text);
    if (status === 'ready' || status === 'invalid' || status === 'unavailable')
      element?.setAttribute('aria-invalid', String(status !== 'ready'));
  }, { flush: 'post' });
  onBeforeUnmount(() => { owner?.abort(); clearTimeout(timer); });
  return { rootReady: computed(() => state.value === 'ready'), rootMessage: message };
}
