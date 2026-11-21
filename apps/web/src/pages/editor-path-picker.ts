import { inject, onBeforeUnmount, provide, watch, type InjectionKey, type WatchSource } from 'vue';
import { desktop } from '../app/desktop';
import { createIdentity } from '../control/identity';

type ChoosePath = (kind: 'file' | 'folder') => Promise<string | undefined>;
const editorPicker: InjectionKey<ChoosePath> = Symbol('editorPathPicker');

export function providePathPicker(session: WatchSource, visible: () => boolean, report: (error: unknown) => void): ChoosePath {
  let pickerId = '', revision = 0;
  function release(): void {
    revision++;
    if (pickerId) void desktop?.releasePathPicker(pickerId).catch(report);
    pickerId = '';
  }
  watch(session, release, { flush: 'sync' });
  watch(visible, () => { revision++; }, { flush: 'sync' });
  onBeforeUnmount(release);
  const choose: ChoosePath = async kind => {
    if (!desktop || !visible()) return;
    pickerId ||= createIdentity();
    const current = revision;
    try {
      const result = await (kind === 'file' ? desktop.chooseFile(pickerId) : desktop.chooseFolder(pickerId));
      if (current === revision && !result.canceled) return result.paths[0];
    } catch (error) {
      if (current === revision) report(error);
    }
  };
  provide(editorPicker, choose);
  return choose;
}

export function usePathPicker(): ChoosePath {
  const choose = inject(editorPicker, undefined);
  let mounted = true;
  onBeforeUnmount(() => { mounted = false; });
  return async kind => {
    const path = await choose?.(kind);
    if (mounted) return path;
  };
}
