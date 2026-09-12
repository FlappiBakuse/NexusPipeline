/** Dispatch 域共享类型：调度中心页面、运行计划弹窗与运行中区块共用同一投影。 */

export interface DispatchPluginSummary {
  name?: string;
  kind?: string;
  capabilities?: string[];
  configuredEnabled?: boolean;
  runtimeEnabled?: boolean;
  state?: string;
  hasFrontend?: boolean;
}

export interface DispatchStatus {
  running?: DispatchRunningRecord[];
  plugins?: DispatchPluginSummary[];
  systemAction?: DispatchSystemAction | null;
}

export interface DispatchLogEntry {
  sequence?: number;
  level?: string;
  text?: string;
}

export interface DispatchRunningRecord {
  id: string;
  targetName?: string;
  kind?: string;
  mode?: string;
  status?: string;
  currentScriptName?: string;
  currentStatus?: string;
  currentAttempt?: number;
  currentMaxAttempts?: number;
  doneTasks?: number;
  totalTasks?: number;
  persistenceWarning?: string;
  logEntries?: DispatchLogEntry[];
  logTail?: string[];
}

export interface DispatchSystemAction {
  action?: string;
  deadline?: string;
  queueName?: string;
}

export interface DispatchPlanTask {
  scriptName?: string;
  taskId?: string;
  userCount?: number;
}

export interface DispatchPlanUser {
  userName?: string;
  status?: string;
  successfulRunsToday?: number;
  maxSuccessfulRunsPerDay?: number;
  reasonCode?: string;
  reasonArgs?: Record<string, unknown>;
}

export interface DispatchPlanResult {
  targetName?: string;
  admissible?: boolean;
  admissionFailure?: { code?: string; args?: Record<string, unknown> };
  tasks?: DispatchPlanTask[];
  users?: DispatchPlanUser[];
  warnings?: Array<{ code?: string; args?: Record<string, unknown> }>;
  totalTasks?: number;
  queueClass?: string;
  completionAction?: string;
}

/** 运行中记录的日志行：优先使用 logEntries，其次回退到 logTail。 */
export function runningLogEntries(record: DispatchRunningRecord): DispatchLogEntry[] {
  if (Array.isArray(record.logEntries)) return record.logEntries;
  return (record.logTail || []).map((text, index) => ({ sequence: index + 1, level: "info", text }));
}

export function runningLogClass(level?: string) {
  const normalized = String(level || "info").toLowerCase();
  return ["debug", "info", "warn", "error", "fatal"].includes(normalized) ? `run-log-${normalized}` : "run-log-info";
}

/** 队列按完成任务数、脚本按尝试次数展示进度百分比。 */
export function runningProgress(record: DispatchRunningRecord) {
  if (record.kind === "queue" && record.totalTasks) return Math.round((Number(record.doneTasks) || 0) / record.totalTasks * 100);
  if (record.currentAttempt && record.currentMaxAttempts) return Math.round(record.currentAttempt / record.currentMaxAttempts * 100);
  return 0;
}
