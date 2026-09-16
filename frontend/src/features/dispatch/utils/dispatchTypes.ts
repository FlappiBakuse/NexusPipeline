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
  timestamp?: string;
  level?: string;
  text?: string;
  message?: string;
  formattedText?: string;
}

export interface DispatchRunningRecord {
  id: string;
  targetId?: string;
  targetName?: string;
  kind?: string;
  mode?: string;
  status?: string;
  currentScriptName?: string;
  currentScriptId?: string;
  currentStatus?: string;
  currentAttempt?: number;
  currentMaxAttempts?: number;
  doneTasks?: number;
  totalTasks?: number;
  persistenceWarning?: string;
  logEntries?: DispatchLogEntry[];
  logTail?: string[];
  logTruncated?: boolean;
}

export interface DispatchSystemAction {
  state?: string;
  action?: string;
  deadline?: string;
  queueName?: string;
}

/** 合并轮询或实时快照，保留已经收到的日志序列与截断标记。 */
export function mergeRunningRecords(
  previous: readonly DispatchRunningRecord[],
  next: readonly DispatchRunningRecord[],
): DispatchRunningRecord[] {
  const previousById = new Map(previous.map(record => [record.id, record]));
  return next.map(record => mergeRunningRecord(previousById.get(record.id), record));
}

function mergeRunningRecord(
  previous: DispatchRunningRecord | undefined,
  next: DispatchRunningRecord,
): DispatchRunningRecord {
  if (!previous) return next;
  const previousEntries = Array.isArray(previous.logEntries) ? previous.logEntries : [];
  const nextEntries = Array.isArray(next.logEntries) ? next.logEntries : [];
  const entries = new Map<number, DispatchLogEntry>();
  for (const entry of [...previousEntries, ...nextEntries]) {
    if (typeof entry.sequence === "number" && Number.isFinite(entry.sequence)) entries.set(entry.sequence, entry);
  }
  const mergedEntries = [...entries.values()]
    .sort((left, right) => (left.sequence || 0) - (right.sequence || 0))
    .slice(-500);
  return {
    ...previous,
    ...next,
    logEntries: mergedEntries.length ? mergedEntries : next.logEntries,
    logTruncated: Boolean(previous.logTruncated || next.logTruncated || entries.size > 500),
  };
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
