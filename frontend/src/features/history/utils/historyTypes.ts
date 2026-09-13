export interface HistoryDate { date: string; count: number }
export type HistoryStatus = "success" | "failed" | "partial" | "cancelled" | "skipped";
export interface HistoryUser { userKey?: string; userId?: string; userName?: string; count?: number; successCount?: number; failedCount?: number; partialCount?: number; cancelledCount?: number; skippedCount?: number }
export interface HistoryScreenshot { id?: string; imageUrl?: string; width?: number; height?: number; trigger?: string }
export interface HistoryAttempt { number: number; status?: string; reason?: string; startTime?: string; endTime?: string; durationMs?: number | null; screenshots?: HistoryScreenshot[] }
export interface HistoryLog { number?: number; logTotalLines?: number; logText?: string; logTail?: string; durationMs?: number | null; screenshots?: HistoryScreenshot[] }
export interface HistoryPlugin { title?: string; id?: string; pluginName?: string; pluginDisplayName?: string; badges?: Array<{ label?: string; tone?: string; title?: string }>; fields?: Array<{ label?: string; value?: string }> }
export interface HistoryRecord {
  id?: string;
  scriptName?: string;
  queueName?: string;
  startTime?: string;
  endTime?: string;
  durationMs?: number | null;
  status?: string;
  resultDetail?: string;
  historyDirectory?: string;
  mode?: string;
  attempts?: number;
  maxAttempts?: number;
  logFile?: string;
  userName?: string;
  attemptDetails?: HistoryAttempt[];
  pluginHistory?: HistoryPlugin[];
}
export interface HistoryDailySummary { date: string; totalCount: number; statusCounts: Partial<Record<HistoryStatus, number>>; totalDurationMs: number; averageDurationMs?: number | null }
export interface HistorySummary { totalCount: number; statusCounts: Partial<Record<HistoryStatus, number>>; totalDurationMs: number; averageDurationMs?: number | null; successRate?: number | null; daily: HistoryDailySummary[] }
export interface HistoryDetailPayload { record?: HistoryRecord; attemptLogs?: HistoryLog[] }
