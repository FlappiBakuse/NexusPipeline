import { ref, watch, onBeforeUnmount } from 'vue';
import type { ConfigurationStore } from '../app/store-ports';
import { extensionList } from '../control/extensions';
import { createIdentity } from '../control/identity';
import { readSource } from './script-source';
import type { ScriptSourceCandidate } from '../control/wire.generated';
import type { ConfigurationOverview } from './configuration-types';

export function useBindingSource(store: ConfigurationStore, overview: () => ConfigurationOverview) {
  const candidates = ref<readonly ScriptSourceCandidate[]>([]), selected = ref(''), revision = ref(0), busy = ref(false);
  const error = ref('');
  let owner: AbortController | undefined;
  const required = () => overview().kind === 'specialized' && overview().resources.length > 0 && overview().resources.every(item => !item.isSaved);
  async function discover(): Promise<void> {
    owner?.abort(); const scope = new AbortController(); owner = scope;
    candidates.value = []; selected.value = ''; revision.value = 0; error.value = '';
    if (!required()) return;
    busy.value = true;
    try {
      const script = store.state.automation?.bundle.scripts.find(item => item.id === overview().scriptId);
      const root = store.state.automation?.roots.find(item => item.id === script?.rootId)?.path;
      if (!root || !script?.extensionId) throw new Error('脚本目录不可用，请刷新后重试。');
      const extensions = await store.client.read('/control/v1/extensions', extensionList, scope.signal);
      const extension = extensions.items.find(item => item.extensionId === script.extensionId && item.isInstalled && item.desiredEnabled && item.activationId && !item.unavailableReason);
      if (!extension) throw new Error('专项插件尚未就绪。');
      const operationId = createIdentity();
      const result = await store.client.query('/control/v1/automation/scripts/discover', {
        format: 'nexus.control.script-source', version: 1, operationId, extensionId: script.extensionId,
        expectedInstalledRevision: extension.installedRevision, rootPath: root
      }, value => readSource(value, operationId, script.extensionId!, root, extension.installedRevision), scope.signal);
      scope.signal.throwIfAborted();
      candidates.value = result.candidates!; revision.value = extension.installedRevision;
      if (!candidates.value.length) error.value = '此目录没有可用的脚本配置。';
      else if (candidates.value.length === 1) selected.value = candidates.value[0]!.candidateId;
    } catch (reason) {
      if (!scope.signal.aborted) error.value = reason instanceof Error ? reason.message : String(reason);
    } finally { if (owner === scope) busy.value = false; }
  }
  watch(() => [overview().bindingId, overview().bindingRevision, overview().scriptRevision, required()], discover, { immediate: true });
  onBeforeUnmount(() => owner?.abort());
  return { candidates, selected, revision, busy, error, required, discover };
}
