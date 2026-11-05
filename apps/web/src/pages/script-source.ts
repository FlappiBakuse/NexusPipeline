import type { AppStore } from '../app/store';
import type { AutomationBundle, ScriptSource, ScriptSourceCandidate, ControlProjectCommand } from '../control/wire.generated';
import { objectValue } from '../control/client';
import { schemaAt, matchesSchema } from '../control/schema';
import { createIdentity } from '../control/identity';
import { argumentText, scriptPaths, scriptPathKey } from './script-paths';
import type { AutomationAction } from './automation-session';

type Script = AutomationBundle['scripts'][number];
export const scriptSourceKey = (id: string): string => 'script-source/' + id;

export function readSource(value: unknown, operationId: string, extensionId: string, root: string, revision: number): ScriptSource {
  const result = objectValue(value), discovery = objectValue(result.discovery);
  if (result.rootPath !== root || result.installedRevision !== revision ||
      discovery.operationId !== operationId || discovery.extensionId !== extensionId || discovery.direction !== 'response' ||
      !Array.isArray(discovery.candidates) || !Array.isArray(discovery.diagnostics) ||
      !matchesSchema(discovery, schemaAt('script-source.schema.json')))
    throw new TypeError('script_source_response_invalid');
  return discovery as unknown as ScriptSource;
}

export function selectSource(store: AppStore, script: Script, row: ScriptSourceCandidate): Script {
  const paths = scriptPaths(store, script);
  paths.executable = row.executable;
  paths.arguments = argumentText(row.arguments);
  paths.configuration = row.resources.map(item => item.path).join('\n');
  paths.logs = row.logPaths.join('\n');
  store.drafts.set(scriptSourceKey(script.id), {
    root: paths.root, extensionId: script.extensionId, selection: row.projectSelection
  });
  store.dirty.add(scriptPathKey(script.id));
  return { ...script, adapterResources: row.resources.map((item, index) => ({
    resourceId: item.resourceId, declarationIndex: index, relativePath: '', definitionRevision: 1,
    syntax: item.syntax, access: { read: [{ selector: [], descendants: true }],
      write: [{ selector: [], descendants: true }], protected: [] }
  })) };
}

export async function saveSourceSelection(store: AppStore, action: AutomationAction, current: () => boolean): Promise<void> {
  if (action.kind !== 'scripts' || action.action !== 'save') return;
  const draft = store.drafts.get(scriptSourceKey(action.target));
  if (!draft?.selection && !action.sourceSelection) return;
  if (!store.can('configuration.edit')) throw new Error('需要配置编辑权限才能保存所选实例。');
  if (!action.sourceSelection) {
    const context = await store.client.query('/control/v1/projects/context', {
      format: 'nexus.control.project-command', version: 1, action: 'context', scriptId: action.target
    }, objectValue);
    if (!current()) throw new Error('script_source_owner_changed');
    if (context.scriptId !== action.target || context.extensionId !== draft?.extensionId ||
        typeof context.scriptRevision !== 'number' || typeof context.selectionRevision !== 'number' ||
        typeof context.providerId !== 'string') throw new TypeError('script_source_context_invalid');
    action.sourceSelection = {
      format: 'nexus.control.project-command', version: 1, action: 'select', operationId: createIdentity(),
      scriptId: action.target, expectedScriptRevision: context.scriptRevision,
      expectedSelectionRevision: context.selectionRevision, extensionId: context.extensionId as string,
      providerId: context.providerId, selection: draft!.selection as Record<string, unknown>
    };
  }
  const command: ControlProjectCommand = action.sourceSelection;
  await store.client.write('/control/v1/projects/select', command, value => {
    const result = objectValue(value);
    if (result.operationId !== command.operationId || result.revision !== command.expectedSelectionRevision! + 1)
      throw new TypeError('script_source_selection_invalid');
    return result;
  }, command.operationId);
}
