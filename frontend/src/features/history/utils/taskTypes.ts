export type TaskTextRef = { kind: 'literal'; value: string } | {
  kind: 'plugin'; key: string; args: Record<string, string | number | boolean>; fallback: string;
};
export interface TaskDisplaySnapshot {
  pluginId: string; pluginVersion: string; defaultLocale: string; localizationHash: string;
  messages: Record<string, Record<string, string>>;
}
export interface TaskDefinition {
  id: string; name: string; parentId: string | null; role: string; enabled: boolean;
  detection: string; retryRisk: string; order: number; countsAsUnit?: boolean;
  nameText?: TaskTextRef;
}
export interface TaskDiagnostic { code: string; message: string; taskId?: string | null; reasonText?: TaskTextRef }
export interface TaskPlan {
  tasks: TaskDefinition[]; coverage: string; pluginVersion: string; generatedAt: string;
  diagnostics: TaskDiagnostic[];
  displaySnapshot?: TaskDisplaySnapshot;
}
export interface TaskResult {
  taskId: string; status: string; reasonCode: string; lastAttemptId: string;
  reasonText?: TaskTextRef;
  evidence: Array<{ sourceId: string; epoch: number; sequence: number; ruleId: string }>;
}
export interface TaskReport {
  diagnostics?: TaskDiagnostic[];
  runId: string; revision: number; userId: string; scriptInstanceId: string; lifecycleOutcome: string;
  evidenceLines?: Array<{ attemptId: string; sourceId: string; epoch: number; sequence: number; text: string }>;
  schemaVersion: number; originalPlan: TaskPlan; finalTaskResults: TaskResult[];
  displaySnapshot?: TaskDisplaySnapshot;
  incidents?: Array<{ attemptId: string; incident: {
    id: string; taskId: string | null; scopeId: string; executionOrdinal: number;
    kind: string; resolution: 'open' | 'recovered' | 'terminal'; reasonCode: string; reasonText?: TaskTextRef;
    evidence: TaskResult['evidence'];
  } }>;
  summary: { tone: string; outcome: string; counts: Record<string, number>; recovered: boolean };
  attemptReports: Array<{ attemptId: string; number: number; selectedTaskIds: string[]; taskResults: TaskResult[];
    retryDecision?: { decision: string; reasonCode: string; reasonText?: TaskTextRef; expandedUnitIds: string[] } }>;
}
export interface TaskUserSummary { userId: string; tone: string; recordId?: string; reason: string; activeCount?: number }
