import { arrayValue, booleanValue, integerValue, objectValue, stringValue } from '../control/client';
import { titleLocales } from '../control/extension-metadata';

export type AdapterAction = 'discover' | 'propose' | 'previewRepair' | 'applyRepair';
export interface AdapterCandidate {
  index: number;
  resourceId: string;
  editCount: number;
}
export interface AdapterTask {
  taskId: string;
  title: string;
  titleLocales?: Readonly<Record<string, string>>;
  isSelected: boolean;
  isEnabled: boolean;
}
export function adapterTasks(value: unknown): AdapterTask[] {
  return arrayValue(value, (item) => {
    const task = objectValue(item);
    return {
      taskId: stringValue(task.taskId),
      title: stringValue(task.title),
      titleLocales: titleLocales(task.titleLocales),
      isSelected: booleanValue(task.isSelected),
      isEnabled: booleanValue(task.isEnabled)
    };
  });
}
export interface AdapterResult {
  coverage?: { selected: number; projected: number; retrySafe: number; isComplete: boolean; hasBlockingDiagnostics: boolean };
  gamePathCheck?: string;
  tasks: AdapterTask[];
  candidates: AdapterCandidate[];
  diagnostics: { code: string; severity: string }[];
  proposalDigest: string;
  hasChanges: boolean | null;
}
export interface AdapterSource {
  overview: ConfigurationOverview;
  installedRevision: number;
}

export function adapterSelection({ overview, installedRevision }: AdapterSource, operationId: string) {
  return {
    operationId,
    bindingId: overview.bindingId,
    expectedBindingRevision: overview.bindingRevision,
    extensionId: overview.extensionId,
    expectedInstalledRevision: installedRevision,
    resources: overview.resources.map(item => ({
      resourceId: item.resourceId, expectedRevision: item.revision, relativePath: item.relativePath
    }))
  };
}

export function adapterCommand(action: AdapterAction, selection: ReturnType<typeof adapterSelection>, candidate?: AdapterCandidate, proposalDigest?: string) {
  return {
    format: 'nexus.control.adapter-command', version: 1, action, selection,
    ...(candidate ? { candidateIndex: candidate.index, proposalDigest } : {})
  };
}

export function adapterResult(
  value: unknown,
  action: AdapterAction,
  request: Record<string, unknown>,
  candidate: AdapterCandidate | undefined,
  digest: string,
  scriptRevision: number
): AdapterResult {
  const row = objectValue(value);
  const result: AdapterResult = {
    tasks: [],
    candidates: [],
    diagnostics: [],
    proposalDigest: '',
    hasChanges: null
  };
  if (action === 'applyRepair') {
    if (
      row.operationId !== request.operationId ||
      row.bindingId !== request.bindingId ||
      row.resourceId !== candidate?.resourceId
    )
      throw new Error('adapter_result_unbound');
    integerValue(row.revision);
  } else requireSource(objectValue(row.source), request, scriptRevision);
  if (action === 'previewRepair' || action === 'applyRepair') {
    if (typeof row.hasChanges !== 'boolean') throw new Error('configuration_response_invalid');
    if (
      action === 'previewRepair' &&
      (row.candidateIndex !== candidate?.index || row.proposalDigest !== digest)
    )
      throw new Error('adapter_result_unbound');
    result.hasChanges = row.hasChanges;
    return result;
  }
  result.diagnostics = arrayValue(row.diagnostics, (value) => {
    const entry = objectValue(value);
    return { code: stringValue(entry.code), severity: stringValue(entry.severity) };
  });
  if (action === 'discover') {
    const coverage = objectValue(row.coverage);
    result.coverage = { selected: integerValue(coverage.selected), projected: integerValue(coverage.projected),
      retrySafe: integerValue(coverage.retrySafe), isComplete: booleanValue(coverage.isComplete),
      hasBlockingDiagnostics: booleanValue(coverage.hasBlockingDiagnostics) };
    result.gamePathCheck = stringValue(row.gamePathCheck);
  }
  if (action === 'discover')
    result.tasks = adapterTasks(row.tasks);
  else {
    result.proposalDigest = stringValue(row.proposalDigest);
    result.candidates = arrayValue(row.candidates, (value) => {
      const item = objectValue(value);
      return {
        index: integerValue(item.index),
        resourceId: stringValue(item.resourceId),
        editCount: integerValue(item.editCount)
      };
    });
  }
  return result;
}

function requireSource(source: Record<string, unknown>, request: Record<string, unknown>, scriptRevision: number): void {
  if (
    source.bindingId !== request.bindingId ||
    source.bindingRevision !== request.expectedBindingRevision ||
    source.extensionId !== request.extensionId ||
    source.installedRevision !== request.expectedInstalledRevision ||
    source.scriptRevision !== scriptRevision
  )
    throw new Error('adapter_source_unbound');
  const expected = arrayValue(request.resources, objectValue),
    resources = arrayValue(source.resources, objectValue);
  if (
    resources.length !== expected.length ||
    new Set(resources.map((item) => item.resourceId)).size !== resources.length ||
    expected.some(
      (item) =>
        !resources.some(
          (value) =>
            value.resourceId === item.resourceId &&
            value.revision === item.expectedRevision &&
            value.relativePath === item.relativePath
        )
    )
  )
    throw new Error('adapter_source_unbound');
}

export interface ConfigurationResource {
  resourceId: string;
  declarationIndex: number;
  label: string;
  revision: number;
  isSaved: boolean;
  shape: 'missing' | 'file' | 'directory';
  fileCount: number;
  relativePath: string;
  syntax: 'json' | 'jsonWithComments' | 'yaml' | 'opaque' | null;
}
export interface ConfigurationOverview {
  bindingId: string;
  bindingRevision: number;
  scriptId: string;
  scriptRevision: number;
  kind: 'generic' | 'specialized';
  extensionId: string | null;
  resources: ConfigurationResource[];
}
export function configurationOverview(value: unknown): ConfigurationOverview {
  const row = objectValue(value);
  const kind = stringValue(row.kind);
  if (kind !== 'generic' && kind !== 'specialized') throw new TypeError('configuration_kind_invalid');
  return {
    bindingId: stringValue(row.bindingId),
    bindingRevision: integerValue(row.bindingRevision),
    scriptId: stringValue(row.scriptId),
    scriptRevision: integerValue(row.scriptRevision),
    kind,
    extensionId: row.extensionId == null ? null : stringValue(row.extensionId),
    resources: arrayValue(row.resources, (item) => {
      const resource = objectValue(item);
      const shape = stringValue(resource.shape);
      if (shape !== 'missing' && shape !== 'file' && shape !== 'directory')
        throw new TypeError('configuration_shape_invalid');
      const syntax = resource.syntax == null ? null : stringValue(resource.syntax);
      if (syntax !== null && syntax !== 'json' && syntax !== 'jsonWithComments' && syntax !== 'yaml' && syntax !== 'opaque')
        throw new TypeError('configuration_syntax_invalid');
      return {
        resourceId: stringValue(resource.resourceId),
        declarationIndex: integerValue(resource.declarationIndex),
        label: stringValue(resource.label),
        revision: integerValue(resource.revision),
        isSaved: booleanValue(resource.isSaved),
        shape,
        fileCount: integerValue(resource.fileCount),
        relativePath: stringValue(resource.relativePath),
        syntax
      };
    })
  };
}
