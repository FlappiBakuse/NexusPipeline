export interface HistoryDate { date: string; count: number }
export interface HistoryUser { userKey?: string; userId?: string; userName?: string; count?: number }
export interface HistoryScreenshot { id?: string; imageUrl?: string; width?: number; height?: number; trigger?: string }
export interface HistoryAttempt { number: number; status?: string; reason?: string; startTime?: string; endTime?: string; screenshots?: HistoryScreenshot[] }
export interface HistoryLog { number?: number; logTotalLines?: number; logText?: string; logTail?: string; screenshots?: HistoryScreenshot[] }
export interface HistoryPlugin { title?: string; id?: string; pluginName?: string; pluginDisplayName?: string; badges?: Array<{ label?: string; tone?: string; title?: string }>; fields?: Array<{ label?: string; value?: string }> }
export interface HistoryRecord {
  id?: string;
  scriptName?: string;
  queueName?: string;
  startTime?: string;
  endTime?: string;
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
export interface HistoryDetailPayload { record?: HistoryRecord; attemptLogs?: HistoryLog[] }
