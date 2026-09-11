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
  automation?: {
    checkEnabled?: boolean;
    waitingForIdle?: boolean;
    idleBlockCode?: string;
    autoUpdateEnabled?: boolean;
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
