import { createIdentity } from '../control/identity';
import type { AppStore } from '../app/store';
import type {
  AutomationBundle,
  ControlAutomationChange,
  ControlAutomationImport
} from '../control/wire.generated';
import { ControlFailure, objectValue } from '../control/client';
import { matchesSchema, schemaAt } from '../control/schema';
import { argumentText, type ScriptPaths } from './script-paths';
import { automationBundle } from '../control/automation';
import { copyDraft } from '../app/draft-copy';

type Script = AutomationBundle['scripts'][number];
export const templateKey = (id: string): string => 'script-template/' + id;

export async function loadTemplate(
  store: AppStore,
  file: File,
  id: string
): Promise<{
  bundle: AutomationBundle;
  value: Script;
  paths: ScriptPaths;
}> {
  if (file.size > 16 * 1024 * 1024) throw new Error('导入文件超过 16 MiB。');
  let data: unknown;
  try {
    data = JSON.parse(await file.text());
  } catch {
    throw new Error('导入文件无法解析。');
  }
  if (!matchesSchema(data, schemaAt('automation-bundle.schema.json')))
    throw new Error('请选择新版通用脚本模板。');
  const bundle = automationBundle(data);
  if (
    bundle.exportKind !== 'scriptTemplate' ||
    bundle.scripts.length !== 1 ||
    bundle.scripts[0]?.kind !== 'generic' ||
    bundle.users.length ||
    bundle.bindings.length ||
    bundle.queues.length ||
    bundle.schedules.length
  )
    throw new Error('请选择新版通用脚本模板。');
  const script = copyDraft(bundle.scripts[0]);
  const roots = store.state.automation?.roots || [];
  const absolute = (path: Script['executable'] | undefined): string => {
    if (!path) return '';
    const root = roots.find((item) => item.id === path.rootId)?.path;
    return root
      ? root.replace(/[\\/]$/, '') + '\\' + path.relative.replace(/\//g, '\\')
      : path.rootId === script.rootId
        ? path.relative
        : '';
  };
  const names = new Set(store.state.automation?.bundle.scripts.map((item) => item.name) || []);
  let name = script.name;
  for (let suffix = 2; names.has(name); suffix++) name = script.name.slice(0, 110) + ' (' + suffix + ')';
  return {
    bundle,
    value: { ...script, id, name, order: store.state.automation?.bundle.scripts.length || 0 },
    paths: {
      root: roots.find((item) => item.id === script.rootId)?.path || '',
      executable: absolute(script.executable),
      arguments: argumentText(script.arguments),
      configuration: script.configurationPaths.map(absolute).join('\n'),
      logs: script.logPaths.map(absolute).join('\n'),
      gamePath: absolute(script.target?.pc?.launchExecutable),
      gameArguments: argumentText(script.target?.pc?.arguments || []),
      androidTool: absolute(script.target?.android?.tool)
    }
  };
}

export async function previewTemplate(
  store: AppStore,
  script: Script,
  preparedRoots: ControlAutomationChange['roots'],
  template: AutomationBundle,
  expectedRevision: number
): Promise<ControlAutomationImport> {
  const used = new Set([
    script.rootId,
    script.executable.rootId,
    ...script.configurationPaths.map((path) => path.rootId),
    ...script.logPaths.map((path) => path.rootId)
  ]);
  const target = script.target;
  if (target?.pc) {
    used.add(target.pc.launchExecutable.rootId);
    used.add(target.pc.readyExecutable.rootId);
  }
  if (target?.android) {
    used.add(target.android.tool.rootId);
    if (target.android.screenshotTool) used.add(target.android.screenshotTool.rootId);
  }
  const catalog = store.state.automation!;
  const roots = [...used].map((id) => {
    const definition =
      preparedRoots.find((item) => item.id === id) || catalog.bundle.roots.find((item) => item.id === id);
    const path =
      preparedRoots.find((item) => item.id === id)?.path ||
      catalog.roots.find((item) => item.id === id)?.path;
    if (!definition || !path) throw new Error('导入路径核对未通过，请检查路径。');
    return { definition: { id, name: definition.name, kind: definition.kind }, path };
  });
  const command: ControlAutomationImport = {
    format: 'nexus.control.automation-import',
    version: 1,
    action: 'preview',
    operationId: createIdentity(),
    expectedRevision,
    previewDigest: '0'.repeat(64),
    bundle: { ...copyDraft(template), scripts: [script], roots: roots.map((item) => item.definition) },
    roots: roots.map((item) => ({ id: item.definition.id, path: item.path })),
    attachments: []
  };
  let result: Record<string, unknown>;
  try {
    result = await store.client.write(
      '/control/v1/automation/import/preview',
      command,
      objectValue,
      command.operationId
    );
  } catch (reason) {
    if (reason instanceof ControlFailure && reason.uncertain) throw new Error('导入预览未完成，请重试。');
    throw reason;
  }
  if (
    result.operationId !== command.operationId ||
    result.expectedRevision !== expectedRevision ||
    typeof result.previewDigest !== 'string' ||
    !/^[a-f0-9]{64}$/.test(result.previewDigest)
  )
    throw new Error('导入预览与当前草稿不匹配。');
  if (result.canApply !== true) throw new Error('导入路径核对未通过，请检查路径。');
  return { ...command, action: 'apply', previewDigest: result.previewDigest };
}
