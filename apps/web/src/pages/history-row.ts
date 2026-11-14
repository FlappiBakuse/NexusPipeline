import type { RecordRow } from '../control/models';
import { locale, t } from '../app/locale';
import { computed } from 'vue';

// ICU formatters own native data; keep one set for the active UI locale across history views.
const durationFormats = computed(() => [
  new Intl.NumberFormat(locale.value, { maximumFractionDigits: 1 }),
  new Intl.NumberFormat(locale.value, { minimumFractionDigits: 1, maximumFractionDigits: 1 })
]);
const rangeFormat = computed(() => new Intl.DateTimeFormat(locale.value, { year: 'numeric', month: 'short', day: 'numeric', timeZone: 'UTC' }));

export function historyElapsed(value: unknown, kind?: unknown): string {
  if (typeof value !== 'number' || !Number.isFinite(value) || value < 0) return t('未知');
  const milliseconds = Math.round(value);
  const [amount, unit, englishUnit] = milliseconds < 1000 ? [milliseconds, '毫秒', 'ms'] :
    milliseconds < 60000 ? [milliseconds / 1000, '秒', 's'] :
    milliseconds < 3600000 ? [milliseconds / 60000, '分钟', 'min'] : [milliseconds / 3600000, '小时', 'h'];
  return (kind === 'checkpointLowerBound' ? t('至少') + ' ' : '') +
    durationFormats.value[0]!.format(Number(amount)) + ' ' + (locale.value === 'en-US' ? englishUnit : unit);
}

// Range endpoints are calendar dates; formatting must preserve their components across time zones.
export const historyDate = (value: string): string => rangeFormat.value.format(new Date(value + 'T00:00:00Z'));

export function historyRow(row: Pick<RecordRow, 'finalAttempt'> & Partial<RecordRow>): { label: string; tone: string; description: string } {
  const { business, lifecycle } = row.finalAttempt;
  let label = '待核实',
    tone = 'neutral';
  if (lifecycle === 'cancelled') label = '已取消';
  else if (lifecycle === 'interrupted') {
    label = '已中断';
    tone = 'warning';
  } else if (lifecycle === 'blocked') {
    label = '已阻断';
    tone = 'danger';
  } else if (lifecycle === 'skipped') label = '已跳过';
  else if (business === 'succeeded') {
    label = '✓ 完成';
    tone = 'success';
  } else if (business === 'failed') {
    label = '✕ 失败';
    tone = 'danger';
  } else if (business === 'partial') {
    label = '部分完成';
    tone = 'warning';
  }
  const date = new Date(String(row.endedAt));
  const two = (value: number) => String(value).padStart(2, '0');
  const time =
    locale.value === 'zh-CN'
      ? `${date.getFullYear()}年${date.getMonth() + 1}月${date.getDate()}日 ${two(date.getHours())}:${two(date.getMinutes())}:${two(date.getSeconds())}`
      : date.toLocaleString(locale.value);
  return { label: t(label), tone, description: time + ' · ' + t('耗时') + ' ' +
    historyElapsed(row.durationMilliseconds, row.durationKind) };
}

const outcomeNames: Record<string, string> = {
  unknown: '待核实',
  unverified: '待核实',
  succeeded: '成功',
  partial: '部分成功',
  failed: '失败',
  notApplicable: '不适用',
  cancelled: '已取消'
};
export const outcomeName = (value: string): string => t(outcomeNames[value] || value);
export const recordBusiness = (row: RecordRow): string =>
  outcomeName(row.finalAttempt.business);
const lifecycleNames: Record<string, string> = {
  completed: '已完成',
  cancelled: '已取消',
  interrupted: '已中断',
  blocked: '已阻断',
  skipped: '已跳过'
};
export const recordLifecycle = (row: RecordRow): string =>
  t(lifecycleNames[row.finalAttempt.lifecycle] || '待核实');
