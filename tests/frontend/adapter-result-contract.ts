import { adapterResult } from '../../apps/web/src/pages/adapter-results.js';

export function adapterResults(): { checks: string[]; checkCount: number } {
  const checks: string[] = [];
  const request = { operationId: 'operation-a', bindingId: 'binding-a', expectedBindingRevision: 2,
    extensionId: 'extension-a', expectedInstalledRevision: 4,
    resources: [{ resourceId: 'main', expectedRevision: 3, relativePath: 'config.json' }] };
  const source = { bindingId: 'binding-a', bindingRevision: 2, extensionId: 'extension-a', installedRevision: 4,
    resources: [{ resourceId: 'main', revision: 3, relativePath: 'config.json' }] };
  const candidate = { index: 1, resourceId: 'main', editCount: 1 };
  const reject = (body: unknown, action: 'discover' | 'previewRepair' | 'applyRepair', description: string): void => {
    let rejected = false;
    try { adapterResult(body, action, request, candidate, 'digest-a'); } catch { rejected = true; }
    if (!rejected) throw new Error(description);
    checks.push(description);
  };
  const discovery = { source, tasks: [{ taskId: 'task-a', title: 'Daily task', isSelected: true, isEnabled: true,
    titleLocales: { 'zh-CN': '每日任务', 'en-US': 'Daily task' } }], diagnostics: [], gamePathCheck: 'unknown',
    coverage: { selected: 1, projected: 1, retrySafe: 1, isComplete: true, hasBlockingDiagnostics: false } };
  if (adapterResult(discovery, 'discover', request, undefined, '').tasks[0]?.taskId !== 'task-a')
    throw new Error('Bound discovery must remain usable');
  checks.push('a discovery bound to the selected extension, binding and saved resources is accepted');
  const accepted = adapterResult(discovery, 'discover', request, undefined, '');
  if (accepted.coverage?.selected !== 1 || accepted.gamePathCheck !== 'unknown' || accepted.tasks[0]?.titleLocales?.['en-US'] !== 'Daily task')
    throw new Error('Coverage, unknown game path and frozen task translations were not decoded');
  checks.push('coverage and unknown path remain explicit and frozen task locales survive decoding');
  for (const coverage of [{ ...discovery.coverage, selected: '1' }, { ...discovery.coverage, isComplete: 'true' },
    { ...discovery.coverage, hasBlockingDiagnostics: null }])
    reject({ ...discovery, coverage }, 'discover', 'malformed coverage remains rejected: ' + JSON.stringify(coverage));
  for (const change of [{ bindingId: 'other' }, { bindingRevision: 9 }, { extensionId: 'other' },
    { installedRevision: 9 }, { resources: [{ ...source.resources[0], relativePath: 'other.json' }] },
    { resources: [...source.resources, ...source.resources] }]) {
    reject({ ...discovery, source: { ...source, ...change } }, 'discover',
      'discovery rejects a foreign source: ' + Object.keys(change)[0]);
  }
  const preview = { source, proposalDigest: 'digest-a', candidateIndex: 1, hasChanges: true };
  reject({ ...preview, candidateIndex: 2 }, 'previewRepair', 'preview rejects a different candidate');
  reject({ ...preview, proposalDigest: 'digest-b' }, 'previewRepair', 'preview rejects a different proposal');
  const applied = { operationId: 'operation-a', bindingId: 'binding-a', resourceId: 'main', revision: 4, hasChanges: true };
  if (!adapterResult(applied, 'applyRepair', request, candidate, 'digest-a').hasChanges)
    throw new Error('Bound repair must remain usable');
  checks.push('an exact applied operation, binding and resource is accepted');
  for (const change of [{ operationId: 'other' }, { bindingId: 'other' }, { resourceId: 'other' }])
    reject({ ...applied, ...change }, 'applyRepair', 'repair rejects a foreign ' + Object.keys(change)[0]);
  return { checks, checkCount: checks.length };
}
