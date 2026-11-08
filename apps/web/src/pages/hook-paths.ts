import { createIdentity } from '../control/identity';
import type { AutomationStore } from '../app/store-ports';
import type { AutomationBundle, ControlAutomationChange } from '../control/wire.generated';

type Hook = NonNullable<AutomationBundle['bindings'][number]['preHook']>;
type HookValues = { preHook?: Hook; postHook?: Hook; preOnceAfterSuccess?: boolean; postFinalOnly?: boolean };
type Paths = { pre: string; post: string };
export const hookPathKey = (store: AutomationStore, id: string): string =>
  'hook-paths/' + store.state.capabilities?.instanceId + '/' + id;

export function hookPaths(store: AutomationStore, id: string, value: HookValues): Paths {
  const key = hookPathKey(store, id);
  const retained = store.drafts.get(key);
  if (retained) return retained as Paths;
  function absolute(hook?: Hook): string {
    if (!hook) return '';
    const root = store.state.automation?.roots.find((item) => item.id === hook.executable.rootId)?.path;
    return root
      ? root.replace(/[\\/]$/, '') + '\\' + hook.executable.relative.replace(/\//g, '\\')
      : hook.executable.relative;
  }
  const paths = {
    pre: (value.preOnceAfterSuccess ? '%FIRST% ' : '') + absolute(value.preHook),
    post: (value.postFinalOnly ? '%LAST% ' : '') + absolute(value.postHook)
  };
  store.drafts.set(key, paths);
  return store.drafts.get(key) as Paths;
}

export function prepareHooks<T extends HookValues>(
  store: AutomationStore,
  id: string,
  value: T,
  roots: ControlAutomationChange['roots'][number][]
): T {
  const paths = store.drafts.get(hookPathKey(store, id)) as Paths | undefined;
  if (!paths) return value;
  function hook(text: string, previous?: Hook): Hook | undefined {
    const path = text
      .replace(/^%(?:FIRST|LAST)%\s*/i, '')
      .trim()
      .replace(/\\/g, '/');
    if (!path) return undefined;
    if (!/^(?:[a-z]:\/|\/\/[^/]+\/[^/]+\/)/i.test(path)) throw new Error('请输入 Host 上的完整路径。');
    const split = path.lastIndexOf('/'),
      parent = path.slice(0, split);
    const existing = store.state.automation?.roots.find(
      (item) => item.path.replace(/\\/g, '/').replace(/\/$/, '').toLowerCase() === parent.toLowerCase()
    );
    const pending = roots.find(
      (item) => item.path.replace(/\\/g, '/').toLowerCase() === parent.toLowerCase()
    );
    const rootId = existing?.id || pending?.id || createIdentity();
    if (!existing && !pending)
      roots.push({
        id: rootId,
        name: '任务脚本目录',
        kind: 'external',
        path: parent.replace(/\//g, '\\'),
        expectedRevision: 0
      });
    return { executable: { rootId, relative: path.slice(split + 1) }, arguments: previous?.arguments || [] };
  }
  const { preHook: before, postHook: after, ...ordinary } = value;
  const preHook = hook(paths.pre, before),
    postHook = hook(paths.post, after);
  return {
    ...ordinary,
    ...(preHook ? { preHook } : {}),
    ...(postHook ? { postHook } : {}),
    preOnceAfterSuccess: !!preHook && /^%FIRST%/i.test(paths.pre.trim()),
    postFinalOnly: !!postHook && /^%LAST%/i.test(paths.post.trim())
  } as T;
}
