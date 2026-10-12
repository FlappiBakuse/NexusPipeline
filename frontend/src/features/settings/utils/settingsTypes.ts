export type Settings = Record<string, any>;

export interface UpdateStatus {
  state?: string;
  current?: string;
  channel?: string;
  latest?: string;
  available?: boolean;
  checked?: boolean;
  notes?: string;
  progress?: number;
  policyVerified?: boolean | null;
  canDownload?: boolean;
  manualUpdateRequired?: boolean;
  updateBlockCode?: string;
  barrierVersion?: string;
  migrationUrl?: string;
  policyError?: string;
  lastApplyFailure?: {
    transactionId: string;
    targetVersion: string;
    code: string;
    atUtc: string;
  } | null;
  automation?: {
    checkEnabled?: boolean;
    waitingForIdle?: boolean;
    idleBlockCode?: string;
    autoUpdateEnabled?: boolean;
    lastCheckAt?: string | null;
    nextCheckAt?: string | null;
  };
}

export interface DiagnosticCheck {
  id?: string;
  category?: string;
  status?: string;
  summaryCode?: string;
  summaryArgs?: Record<string, unknown>;
  detailCode?: string;
  detailArgs?: Record<string, unknown>;
  remediationCode?: string;
  remediationArgs?: Record<string, unknown>;
}

export interface DiagnosticsData {
  error?: string;
  overallStatus?: string;
  hostVersion?: string;
  checks?: DiagnosticCheck[];
}
