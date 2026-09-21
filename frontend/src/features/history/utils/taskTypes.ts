export interface TaskDefinition {
  id: string; name: string; parentId: string | null; role: string; enabled: boolean;
  detection: string; retryRisk: string; order: number;
}
export interface TaskPlan {
  tasks: TaskDefinition[]; coverage: string; pluginVersion: string; generatedAt: string;
  diagnostics: Array<{ code: string; message: string }>;
}
export interface TaskResult {
  taskId: string; status: string; reasonCode: string; lastAttemptId: string;
  evidence: Array<{ sourceId: string; epoch: number; sequence: number; ruleId: string }>;
}
export interface TaskReport {
  runId: string; revision: number; userId: string; scriptInstanceId: string; lifecycleOutcome: string;
  evidenceLines?: Array<{ attemptId: string; sourceId: string; epoch: number; sequence: number; text: string }>;
  schemaVersion: number; originalPlan: TaskPlan; finalTaskResults: TaskResult[];
  summary: { tone: string; outcome: string; counts: Record<string, number>; recovered: boolean };
  attemptReports: Array<{ attemptId: string; number: number; selectedTaskIds: string[]; taskResults: TaskResult[];
    retryDecision?: { decision: string; reasonCode: string; expandedUnitIds: string[] } }>;
}
export interface TaskUserSummary { userId: string; tone: string; recordId?: string; reason: string; activeCount?: number }
