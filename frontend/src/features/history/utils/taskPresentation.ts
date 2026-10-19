import { getLocale, t } from '../../../platform/i18n';
import { resolveTaskText } from './taskText';
import type { TaskDisplaySnapshot, TaskReport, TaskResult, TaskTextRef } from './taskTypes';

export type TaskProof = Pick<TaskResult, 'evidence' | 'hostEvidenceRefs' | 'structuredEvidenceRefs'>;
export type StructuredProof = NonNullable<TaskReport['structuredEvidence']>[number];

export function taskReason(code: string, text?: TaskTextRef, snapshot?: TaskDisplaySnapshot): string {
  return text ? resolveTaskText(text, snapshot, getLocale(), t('tasks.reason.other'))
    : t(`tasks.reason.${code}`, {}, t('tasks.reason.other'));
}

export function structuredProofText(proof: StructuredProof | undefined, report: TaskReport): string {
  if (!proof) return t('tasks.presentation.evidence_missing');
  if (proof.kind === 'task_event' && ['running', 'succeeded', 'failed'].includes(proof.status || '')) {
    const basis = report.resultBasis === 'framework-task-v1' ? 'framework' : 'upstream';
    return t(`tasks.presentation.proof.${basis}.${proof.status}`);
  }
  if (['ready', 'cancel_ack', 'fault'].includes(proof.kind)) return t(`tasks.presentation.proof.${proof.kind}`);
  if (proof.kind === 'completed' && ['succeeded', 'failed'].includes(proof.status || ''))
    return t(`tasks.presentation.proof.completed.${proof.status}`);
  return t('tasks.presentation.proof.unknown');
}

export function hasTaskProof(result: TaskProof): boolean {
  return Boolean(result.evidence.length || result.hostEvidenceRefs?.length || result.structuredEvidenceRefs?.length);
}
