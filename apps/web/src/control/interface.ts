import { arrayValue, integerValue, objectValue, stringValue } from './client';

export type DashboardCard = 'game-activities' | 'status' | 'running' | 'history-duration' | 'history-counts';
export type InterfaceCategory = 'service' | 'remote' | 'proxy' | 'advanced' | 'updates' | 'diagnostics';
export interface InterfaceValues {
  theme: 'dark' | 'light' | 'system';
  locale: 'zh-CN' | 'en-US';
  dashboardCards: DashboardCard[];
  pinnedSettings: InterfaceCategory[];
  collapsedSettings: InterfaceCategory[];
}
export interface InterfaceSnapshot {
  revision: number;
  values: InterfaceValues;
  operationId: string;
}
export const dashboardDefaults: DashboardCard[] = ['game-activities', 'status', 'running', 'history-duration'];
const cards: DashboardCard[] = ['game-activities', 'status', 'running', 'history-duration', 'history-counts'];
const categories: InterfaceCategory[] = ['service', 'remote', 'proxy', 'advanced', 'updates', 'diagnostics'];
export function dashboardCards(values: readonly DashboardCard[]): DashboardCard[] {
  // Both stored chart identifiers share one visible unit; proof checks still need the original wire values.
  return [
    ...new Set(values.map((value) => (value === 'history-counts' ? ('history-duration' as const) : value)))
  ];
}
function selected<T extends string>(value: unknown, available: T[]): T {
  const candidate = stringValue(value);
  if (!available.includes(candidate as T)) throw new TypeError('interface_value_invalid');
  return candidate as T;
}
function selectedList<T extends string>(value: unknown, available: T[]): T[] {
  const values = arrayValue(value, (item) => selected(item, available));
  if (new Set(values).size !== values.length) throw new TypeError('interface_value_duplicated');
  return values;
}
export function interfaceSnapshot(value: unknown): InterfaceSnapshot {
  const row = objectValue(value),
    values = objectValue(row.values);
  return {
    revision: integerValue(row.revision),
    operationId: stringValue(row.operationId),
    values: {
      theme: selected(values.theme, ['dark', 'light', 'system']),
      locale: selected(values.locale, ['zh-CN', 'en-US']),
      dashboardCards: selectedList(values.dashboardCards, cards),
      pinnedSettings: selectedList(values.pinnedSettings, categories),
      collapsedSettings: selectedList(values.collapsedSettings, categories)
    }
  };
}
