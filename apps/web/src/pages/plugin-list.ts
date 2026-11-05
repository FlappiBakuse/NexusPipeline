import { pinyin } from 'pinyin-pro';
import type { ExtensionMetadata } from '../control/extension-metadata';

export interface PluginIndex { raw: string[]; romanized: { full: string; initials: string }[] }
export interface PluginListRow {
  extensionId: string; name: string; summary: string; kind: string; artifactName?: string; metadata?: ExtensionMetadata;
}
const normalize = (value: string) => value.normalize('NFKC').toLocaleLowerCase('zh-CN');
const roman = (value: string) => normalize(value).normalize('NFD').replace(/\p{M}/gu, '').replace(/ü/g, 'v').replace(/[\s\-_.·'’]+/g, '');
const han = /\p{Script=Han}/u;
export function pluginIndex(row: PluginListRow): PluginIndex {
  const metadata = row.metadata;
  const names = metadata ? [metadata.displayName, metadata.gameName,
    ...Object.values(metadata.locales).flatMap(value => [value.displayName, value.gameName])] : [row.name];
  names.push(row.extensionId, row.artifactName || '');
  const raw = [...names, metadata?.summary || row.summary, metadata?.author || '', ...(metadata?.tags || []),
    ...Object.values(metadata?.locales || {}).map(value => value.summary)].map(normalize);
  return { raw, romanized: names.filter(name => han.test(name)).map(name => {
    const syllables = pinyin(name, { type: 'array', toneType: 'none', nonZh: 'consecutive' });
    return { full: roman(syllables.join('')), initials: roman(syllables.map(value => value[0] || '').join('')) };
  }) };
}
export function matchesPlugin(index: PluginIndex, query: string): boolean {
  const value = normalize(query.trim());
  if (!value || index.raw.some(field => field.includes(value))) return true;
  if (han.test(value)) return false;
  const compact = roman(value);
  return !!compact && index.romanized.some(name => name.full.includes(compact) || name.initials.includes(compact));
}
export function comparePlugins(left: PluginListRow, right: PluginListRow, field: string, descending: boolean): number {
  const direction = descending ? -1 : 1;
  if (field === 'created' || field === 'updated') {
    const first = left.metadata?.[field === 'created' ? 'createdAt' : 'updatedAt'];
    const second = right.metadata?.[field === 'created' ? 'createdAt' : 'updatedAt'];
    if (!first && second) return 1;
    if (first && !second) return -1;
    if (first && second) {
      const order = Date.parse(first) - Date.parse(second);
      if (order) return order * direction;
    }
  }
  const result = new Intl.Collator('zh-CN-u-co-pinyin', { numeric: true, sensitivity: 'base' }).compare(left.name, right.name);
  return (field === 'name' ? result * direction : result) || left.extensionId.localeCompare(right.extensionId, 'en');
}
