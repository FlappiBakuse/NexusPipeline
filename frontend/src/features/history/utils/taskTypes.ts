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
  completionPolicy?: 'flow' | 'authoritative';
  workflowRole?: 'daily' | 'technical' | 'manual_only';
  observationContract?: { ruleSetId: string; sources: string[]; rules: Array<{ id: string; kind: string }> };
  retryPolicy?: { mode: 'native_resume' | 'selective_config'; resourceConsumption: boolean; limitRefs: string[] };
}
export interface TaskDiagnostic { code: string; message: string; taskId?: string | null; reasonText?: TaskTextRef }
export interface TaskConfigCheck {
  ruleId: string; evaluation: 'satisfied' | 'violated' | 'unknown' | 'not_applicable';
  severity: 'info' | 'warning' | 'error'; executionEffect: 'none' | 'warn' | 'block';
  scope: { kind: string; taskId?: string }; locations: Array<Record<string, unknown>>;
  actions: Array<{ kind: string }>; reasonText?: TaskTextRef;
}
export interface TaskConfigAssessment { schemaVersion: string; checks: TaskConfigCheck[] }
export interface TaskReadiness {
  state: 'ready' | 'attention' | 'unknown' | 'blocked'; stale: boolean; checkedAt: string;
  assessmentId: string; configRevision: string; contextFingerprint: string;
}
export interface TaskPlan {
  protocolVersion?: string;
  semanticsVersion?: string;
  tasks: TaskDefinition[]; coverage: string; pluginVersion: string; generatedAt: string;
  diagnostics: TaskDiagnostic[];
  displaySnapshot?: TaskDisplaySnapshot;
  configAssessment?: TaskConfigAssessment;
  currentReadiness?: TaskReadiness;
}
export interface TaskResult {
  taskId: string; status: string; reasonCode: string; lastAttemptId: string;
  reasonText?: TaskTextRef;
  engineStatus?: string;
  structuredEvidenceRefs?: string[];
  hostEvidenceRefs?: string[];
  evidence: Array<{ sourceId: string; epoch: number; sequence: number; ruleId: string }>;
}
export interface TaskReport {
  semanticsVersion?: string;
  hostEvidence?: Array<{ kind: string; attemptId: string; hostEventId: string; taskIds: string[] }>;
  engineStatus?: string;
  businessVerification?: string;
  structuredEvidenceVersion?: number;
  structuredEvidence?: Array<{ id: string; providerId: string; sessionId?: string; attemptId: string;
    sourceSequence: number; kind: string; taskId?: string; status?: string; nativeTaskId?: number | string }>;
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
  summary?: { tone: string; outcome: string; counts: Record<string, number>; recovered: boolean };
  admissionBlocked?: { reasonCode: string; message: string; readiness?: TaskReadiness; configAssessment?: TaskConfigAssessment };
  attemptReports: Array<{ attemptId: string; number: number; selectedTaskIds: string[]; taskResults: TaskResult[];
    retryDecision?: { decision: string; reasonCode: string; reasonText?: TaskTextRef; expandedUnitIds: string[] } }>;
}
export interface TaskUserSummary {
  userId: string; tone: string; recordId?: string; reason: string; activeCount?: number;
  admissionRecordId?: string; admissionState?: 'ready' | 'attention' | 'unknown' | 'blocked'; admissionReason?: string;
}
