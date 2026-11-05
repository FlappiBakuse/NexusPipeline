import { arrayValue, booleanValue, boundedInteger, dateValue, enumValue, integerValue, objectValue, stringValue, uuidValue } from './client';
import type { AutomationBundle } from './wire.generated';
import { attemptFacts, type AttemptFacts } from './records';
import { titleLocales } from './extension-metadata';

export interface RunTask {
  taskId: string;
  title: string;
  titleLocales?: Readonly<Record<string, string>>;
  isSelected: boolean;
  isEnabled: boolean;
  dependencies: string[];
  state: 'succeeded' | 'partial' | 'failed' | 'unknown';
  reasonCode?: string;
  workflowRole?: 'business' | 'technical' | 'cleanup';
  completionKind?: 'completed' | 'satisfied' | 'inapplicable' | 'notExecuted';
}

export interface ActiveStep {
  stepId: string;
  scriptId: string;
  userId: string | null;
  bindingId: string | null;
  dependsOn: string[];
  phase: string;
  tasks: RunTask[];
  logRoles: string[];
  maximumInvocations?: number;
  invocations?: { ordinal: number; engine: string; business: string; reasonCode: string;
    startedAt: string | null; endedAt: string | null; durationMilliseconds: number | null }[];
  engine?: string;
  business?: string;
  reasonCode?: string;
}

export interface RunDisplay {
  targetKind: string;
  targetId: string;
  targetName: string;
  mode: string;
  maximumAttempts: number;
}

export interface ActiveRun {
  operationId: string;
  runId: string;
  attemptId: string;
  ordinal: number;
  phase: string;
  canCancel: boolean;
  cancellationRequested: boolean;
  steps: ActiveStep[];
  accounts?: { userId: string; facts: AttemptFacts }[];
  display?: RunDisplay;
}

export function executionRun(value: unknown): ActiveRun {
  const row = objectValue(value);
  const display = row.display == null ? undefined : objectValue(row.display);
  return {
    operationId: uuidValue(row.operationId),
    runId: uuidValue(row.runId),
    attemptId: uuidValue(row.attemptId),
    ordinal: boundedInteger(row.ordinal, 1, 99),
    phase: enumValue(row.phase, ['preparing', 'running', 'collecting', 'collected', 'restoring', 'restored', 'skipped', 'stopping', 'committed', 'reconciliationRequired']),
    canCancel: booleanValue(row.canCancel),
    cancellationRequested: booleanValue(row.cancellationRequested),
    ...(display
      ? {
          display: {
            targetKind: enumValue(display.targetKind, ['script', 'queue', 'projectInitialization']),
            targetId: stringValue(display.targetId),
            targetName: stringValue(display.targetName),
            mode: enumValue(display.mode, ['manual', 'auto']),
            maximumAttempts: boundedInteger(display.maximumAttempts, 1, 99)
          }
        }
      : {}),
    steps: arrayValue(row.steps, executionStep),
    ...(row.accounts == null ? {} : { accounts: arrayValue(row.accounts, value => {
      const account = objectValue(value), facts = attemptFacts(account.facts);
      if (facts.runId !== row.runId || facts.attemptId !== row.attemptId) throw new TypeError('execution_account_unbound');
      return { userId: uuidValue(account.userId), facts };
    }) })
  };
}

export function executionStep(value: unknown): ActiveStep {
      const step = objectValue(value);
      return {
        stepId: stringValue(step.stepId),
        scriptId: stringValue(step.scriptId),
        userId: step.userId == null ? null : stringValue(step.userId),
        bindingId: step.bindingId == null ? null : stringValue(step.bindingId),
        dependsOn: arrayValue(step.dependsOn, stringValue),
        phase: enumValue(step.phase, ['pending', 'preparing', 'running', 'collecting', 'collected', 'restoring', 'restored', 'skipped', 'notExecuted']),
        ...(step.engine == null ? {} : { engine: enumValue(step.engine, ['NotStarted', 'Running', 'Succeeded', 'Failed', 'Cancelled', 'Unknown']) }),
        ...(step.business == null ? {} : { business: enumValue(step.business, ['Unverified', 'Succeeded', 'Partial', 'Failed', 'NotApplicable', 'Cancelled']) }),
        ...(step.reasonCode == null ? {} : { reasonCode: stringValue(step.reasonCode) }),
        logRoles: arrayValue(step.logRoles, stringValue),
        ...(step.maximumInvocations == null ? {} : { maximumInvocations: boundedInteger(step.maximumInvocations, 1, 99) }),
        ...(step.invocations == null ? {} : { invocations: arrayValue(step.invocations, value => {
          const item = objectValue(value);
          return { ordinal: boundedInteger(item.ordinal, 1, 99), engine: stringValue(item.engine),
            business: stringValue(item.business), reasonCode: stringValue(item.reasonCode),
            startedAt: item.startedAt == null ? null : dateValue(item.startedAt),
            endedAt: item.endedAt == null ? null : dateValue(item.endedAt),
            durationMilliseconds: item.durationMilliseconds == null ? null : boundedInteger(item.durationMilliseconds, 0, 31536000000) };
        }) }),
        tasks: arrayValue(step.tasks, (value) => {
          const task = objectValue(value);
          return {
            taskId: stringValue(task.taskId),
            title: stringValue(task.title),
            titleLocales: titleLocales(task.titleLocales),
            isSelected: booleanValue(task.isSelected),
            isEnabled: booleanValue(task.isEnabled),
            dependencies: arrayValue(task.dependencies, stringValue),
            state: enumValue(task.state, ['succeeded', 'partial', 'failed', 'unknown']),
            ...(task.workflowRole == null ? {} : { workflowRole: enumValue(task.workflowRole, ['business', 'technical', 'cleanup']) }),
            ...(task.completionKind == null ? {} : { completionKind: enumValue(task.completionKind, ['completed', 'satisfied', 'inapplicable', 'notExecuted']) }),
            ...(task.reasonCode == null ? {} : { reasonCode: stringValue(task.reasonCode) })
          };
        })
      };
}

export interface ExecutionPreview {
  hasRunnable: boolean;
  hasBlockedItems: boolean;
  reasonCode: string | null;
  completionAction: AutomationBundle['queues'][number]['completionAction'];
  groups: { scriptId: string }[];
  items: { scriptId: string; userId: string; disposition: 'runnable' | 'skipped' | 'blocked'; reasonCode: string;
    successfulRunsToday?: number; maximumSuccessfulRunsPerDay?: number }[];
}
export function executionPreview(value: unknown): ExecutionPreview {
  const row = objectValue(value);
  return { hasRunnable: booleanValue(row.hasRunnable), hasBlockedItems: booleanValue(row.hasBlockedItems),
    reasonCode: row.reasonCode === null ? null : stringValue(row.reasonCode),
    completionAction: row.completionAction === 'exitHost' ? 'exit' : enumValue(row.completionAction, ['none', 'shutdown', 'hibernate', 'restart', 'exit']),
    groups: arrayValue(row.groups, value => ({ scriptId: stringValue(objectValue(value).scriptId) })),
    items: arrayValue(row.items, value => {
      const item = objectValue(value);
      return { scriptId: stringValue(item.scriptId), userId: stringValue(item.userId),
        disposition: enumValue(item.disposition, ['runnable', 'skipped', 'blocked']), reasonCode: stringValue(item.reasonCode),
        ...(item.successfulRunsToday == null ? {} : { successfulRunsToday: integerValue(item.successfulRunsToday) }),
        ...(item.maximumSuccessfulRunsPerDay == null ? {} : { maximumSuccessfulRunsPerDay: boundedInteger(item.maximumSuccessfulRunsPerDay, -1, 9999) }) };
    }) };
}

export function currentStep(run: ActiveRun): ActiveStep | undefined {
  return (
    run.steps.find((step) => !['pending', 'restored', 'skipped'].includes(step.phase)) ?? run.steps.at(-1)
  );
}

export function finishedSteps(run: ActiveRun): number {
  return run.steps.filter((step) => step.phase === 'restored' || step.phase === 'skipped').length;
}
