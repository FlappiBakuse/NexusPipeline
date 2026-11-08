import { createIdentity } from '../control/identity';
import type { AutomationStore } from '../app/store-ports';
import type { AutomationBundle, ControlAutomationChange } from '../control/wire.generated';

type Script = AutomationBundle['scripts'][number];
export type ScriptPaths = {
  root: string;
  executable: string;
  configuration: string;
  logs: string;
  arguments: string;
  gamePath: string;
  gameArguments: string;
  androidTool: string;
  emulatorRoot?: { id: string; name: string; path: string; isDeclared: boolean };
  judgeLanguage?: 'javascript' | 'python';
};
export const scriptPathKey = (id: string): string => 'script-paths/' + id;
const normalized = (path: string): string => path.trim().replace(/\\/g, '/').replace(/\/+$/, '');

export function argumentText(values: readonly string[]): string {
  return values
    .map((value) =>
      !value || /[\s"]/u.test(value)
        ? '"' + value.replace(/(\\*)"/g, '$1$1\\"').replace(/(\\+)$/g, '$1$1') + '"'
        : value
    )
    .join(' ');
}

export function argumentValues(text: string): string[] {
  const values: string[] = [];
  let value = '',
    quoted = false,
    started = false;
  for (let index = 0; index < text.length; index++) {
    const char = text[index]!;
    if (char === '\\') {
      let end = index;
      while (text[end] === '\\') end++;
      const count = end - index;
      if (text[end] === '"') {
        value += '\\'.repeat(Math.floor(count / 2));
        if (count % 2) value += '"';
        else quoted = !quoted;
        index = end;
        started = true;
        continue;
      }
      value += '\\'.repeat(count);
      index = end - 1;
      started = true;
      continue;
    }
    if (char === '"') {
      quoted = !quoted;
      started = true;
      continue;
    }
    if (/\s/u.test(char) && !quoted) {
      if (started) values.push(value);
      value = '';
      started = false;
    } else {
      value += char;
      started = true;
    }
  }
  if (quoted) throw new Error('启动参数的引号未闭合。');
  if (started) values.push(value);
  return values;
}

export function scriptPaths(store: AutomationStore, script: Script): ScriptPaths {
  const key = scriptPathKey(script.id);
  const saved = store.drafts.get(key);
  if (saved) return saved as ScriptPaths;
  const roots = store.state.automation?.roots || [];
  const absolute = (path: Script['executable']): string => {
    const root = roots.find((item) => item.id === path.rootId)?.path;
    return root ? root.replace(/[\\/]$/, '') + '\\' + path.relative.replace(/\//g, '\\') : path.relative;
  };
  const state = {
    root: roots.find((item) => item.id === script.rootId)?.path || '',
    executable: absolute(script.executable),
    configuration: script.configurationPaths.map(absolute).join('\n'),
    logs: script.logPaths.map(absolute).join('\n'),
    arguments: argumentText(script.arguments),
    gamePath: script.target?.pc ? absolute(script.target.pc.launchExecutable) : '',
    gameArguments: argumentText(script.target?.pc?.arguments || []),
    androidTool: script.target?.android ? absolute(script.target.android.tool) : ''
  };
  store.drafts.set(key, state);
  return store.drafts.get(key) as ScriptPaths;
}

export function prepareScript(
  store: AutomationStore,
  value: Script
): { value: Script; roots: ControlAutomationChange['roots'] } {
  const state = store.drafts.get(scriptPathKey(value.id)) as ScriptPaths | undefined;
  if (!state) return { value, roots: [] };
  const catalog = store.state.automation!;
  const roots: ControlAutomationChange['roots'][number][] = [];
  function root(path: string, kind: 'script' | 'external'): string {
    const clean = normalized(path);
    if (!/^(?:[a-z]:\/|\/\/[^/]+\/[^/]+)/i.test(clean)) throw new Error('请输入 Host 上的完整路径。');
    const existing = catalog.roots.find(
      (item) =>
        normalized(item.path).toLowerCase() === clean.toLowerCase() &&
        catalog.bundle.roots.some((value) => value.id === item.id && value.kind === kind)
    );
    if (existing) return existing.id;
    const prepared = roots.find(
      (item) => normalized(item.path).toLowerCase() === clean.toLowerCase() && item.kind === kind
    );
    if (prepared) return prepared.id;
    const id = createIdentity();
    roots.push({
      id,
      name: (value.name || '脚本') + (kind === 'script' ? ' · 根目录' : ' · 外部目录'),
      kind,
      path: clean.replace(/\//g, '\\'),
      expectedRevision: 0
    });
    return id;
  }
  const rootPath = normalized(state.root),
    rootId = root(rootPath, 'script');
  function portable(path: string): Script['executable'] {
    let clean = normalized(path);
    if (!clean) throw new Error('请填写主程序路径。');
    if (!/^(?:[a-z]:\/|\/\/)/i.test(clean)) return { rootId, relative: clean };
    if (clean.toLowerCase().startsWith(rootPath.toLowerCase() + '/'))
      return { rootId, relative: clean.slice(rootPath.length + 1) };
    const split = clean.lastIndexOf('/');
    return { rootId: root(clean.slice(0, split), 'external'), relative: clean.slice(split + 1) };
  }
  const paths = (text: string) =>
    text
      .split(/\r?\n/)
      .filter((item) => item.trim())
      .map(portable);
  let target = value.target;
  if (target?.pc && !state.gamePath.trim() && !target.launchByHost && !target.forceCloseAtEnd)
    target = undefined;
  if (target?.pc)
    target = {
      ...target,
      pc: {
        ...target.pc,
        launchExecutable: portable(state.gamePath),
        readyExecutable: portable(state.gamePath),
        arguments: argumentValues(state.gameArguments)
      }
    };
  if (target?.android)
  {
    const selected = target.android.emulator, selectedRoot = state.emulatorRoot;
    let tool = portable(state.androidTool);
    if (selected && selectedRoot && selectedRoot.id === selected.rootId) {
      const prefix = normalized(selectedRoot.path) + '/';
      const absolute = normalized(state.androidTool);
      if (!absolute.toLowerCase().startsWith(prefix.toLowerCase())) throw new Error('emulator_adb_selection_changed');
      tool = { rootId: selectedRoot.id, relative: absolute.slice(prefix.length) };
      const redundant = roots.findIndex(root => root.id !== selectedRoot.id && normalized(root.path).toLowerCase() === normalized(absolute.slice(0, absolute.lastIndexOf('/'))).toLowerCase());
      if (redundant >= 0) roots.splice(redundant, 1);
      if (!selectedRoot.isDeclared && !catalog.roots.some(root => root.id === selectedRoot.id)) roots.push({ id: selectedRoot.id,
        name: selectedRoot.name, path: selectedRoot.path, kind: 'external', expectedRevision: 0 });
    } else if (selected) {
      const existing = catalog.roots.find(root => root.id === selected.rootId);
      if (!existing || !normalized(state.androidTool).toLowerCase().startsWith(normalized(existing.path).toLowerCase() + '/')) throw new Error('emulator_adb_selection_changed');
      tool = { rootId: existing.id, relative: normalized(state.androidTool).slice(normalized(existing.path).length + 1) };
    }
    target = {
      ...target,
      android: {
        ...target.android,
        tool,
        ...(target.android.screenshotTool
          ? { screenshotTool: portable(target.android.screenshotTool.relative) }
          : {})
      }
    };
  }
  const { target: omitted, ...ordinary } = value;
  return {
    roots,
    value: {
      ...ordinary,
      ...(target ? { target } : {}),
      rootId,
      executable: portable(state.executable),
      arguments: argumentValues(state.arguments),
      configurationPaths: paths(state.configuration),
      logPaths: paths(state.logs)
    }
  };
}
