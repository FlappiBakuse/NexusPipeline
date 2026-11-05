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
export interface AdapterResult {
  coverage?: { selected: number; projected: number; retrySafe: number; isComplete: boolean; hasBlockingDiagnostics: boolean };
  gamePathCheck?: string;
  tasks: AdapterTask[];
  candidates: AdapterCandidate[];
  diagnostics: { code: string; severity: string }[];
  proposalDigest: string;
  hasChanges: boolean | null;
}
export type AdapterDraft = Omit<AdapterResult, 'hasChanges'> & {
  resourceIds: string[];
  operationId: string;
  candidateIndex: number;
  previewed: boolean;
  pending: string;
  pendingAction: AdapterAction | '';
  request: Record<string, unknown> | null;
  busy: boolean;
  note: string;
};

export function resetAdapterProposal(
  state: Pick<AdapterResult, 'tasks' | 'candidates' | 'diagnostics' | 'proposalDigest'> & {
    operationId: string;
    candidateIndex: number;
    previewed: boolean;
    request: Record<string, unknown> | null;
    note: string;
    coverage?: AdapterResult['coverage'];
    gamePathCheck?: string;
  }
): void {
  state.operationId = '';
  state.proposalDigest = '';
  state.candidateIndex = -1;
  state.previewed = false;
  state.request = null;
  state.candidates = [];
  state.tasks = [];
  state.coverage = undefined;
  state.gamePathCheck = undefined;
  state.diagnostics = [];
  state.note = '';
}

export function adapterResult(
  value: unknown,
  action: AdapterAction,
  request: Record<string, unknown>,
  candidate: AdapterCandidate | undefined,
  digest: string
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
  } else requireSource(objectValue(row.source), request);
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
    result.tasks = arrayValue(row.tasks, (value) => {
      const task = objectValue(value);
      return {
        taskId: stringValue(task.taskId),
        title: stringValue(task.title),
        titleLocales: titleLocales(task.titleLocales),
        isSelected: booleanValue(task.isSelected),
        isEnabled: booleanValue(task.isEnabled)
      };
    });
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

function requireSource(source: Record<string, unknown>, request: Record<string, unknown>): void {
  if (
    source.bindingId !== request.bindingId ||
    source.bindingRevision !== request.expectedBindingRevision ||
    source.extensionId !== request.extensionId ||
    source.installedRevision !== request.expectedInstalledRevision
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
