export type { ActiveStep, ActiveRun } from '../control/execution';
export interface RecordTotals {
  runs: number;
  succeeded: number;
  failed: number;
  cancelled: number;
  unverified: number;
  measuredCount: number;
  measuredMilliseconds: number;
  lowerBoundCount: number;
  unknownDurationCount: number;
}
export interface RecordSummary {
  totals: RecordTotals;
  days: { date: string; totals: RecordTotals }[];
}
