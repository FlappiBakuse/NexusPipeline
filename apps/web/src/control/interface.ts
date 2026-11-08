import { arrayValue, integerValue, objectValue, stringValue } from './client';

export type DashboardCard = string;
export type InterfaceCategory = 'service' | 'remote' | 'proxy' | 'advanced' | 'updates' | 'diagnostics' | `extension:${string}:${string}`;
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
export const dashboardDefaults: DashboardCard[] = ['status', 'running', 'history-duration'];
function cardKeys(value: unknown): DashboardCard[] {
  const values = arrayValue(value, stringValue);
  if (values.length > 64 || new Set(values).size !== values.length ||
    values.some(key => !/^[a-z][a-z0-9.-]{0,95}$/.test(key))) throw new TypeError('interface_cards_invalid');
  return values;
}
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
function settingsCategories(value: unknown): InterfaceCategory[] {
  const values = arrayValue(value, item => {
    const id = stringValue(item);
    if (!categories.includes(id as InterfaceCategory) && !/^extension:[a-z0-9-]{1,80}:[a-z0-9-]{1,40}$/.test(id))
      throw new TypeError('interface_value_invalid');
    return id as InterfaceCategory;
  });
  if (values.length > 64 || new Set(values).size !== values.length) throw new TypeError('interface_value_duplicated');
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
      dashboardCards: cardKeys(values.dashboardCards),
      pinnedSettings: settingsCategories(values.pinnedSettings),
      collapsedSettings: settingsCategories(values.collapsedSettings)
    }
  };
}
