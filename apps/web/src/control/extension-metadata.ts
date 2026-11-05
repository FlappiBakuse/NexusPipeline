import { arrayValue, dateValue, objectValue, stringValue } from './client';
export interface ExtensionText { displayName: string; gameName: string; summary: string }
export interface ExtensionMetadata extends ExtensionText {
  defaultLocale: string;
  author?: string;
  tags: string[];
  locales: Readonly<Record<string, ExtensionText>>;
  createdAt: string | null;
  updatedAt: string | null;
}
const localeName = /^[a-z]{2,3}(?:-[A-Z][a-z]{3})?(?:-[A-Z]{2}|-[0-9]{3})?$/;
function text(value: unknown, maximum: number, required = false): string {
  const result = stringValue(value);
  if (result.length > maximum || required && !result.trim()) throw new TypeError('extension_text_invalid');
  return result;
}
function localizedText(value: unknown): ExtensionText {
  const row = objectValue(value);
  return { displayName: text(row.displayName, 120, true), gameName: text(row.gameName, 120), summary: text(row.summary, 1024) };
}
export function extensionMetadata(value: unknown): ExtensionMetadata | undefined {
  if (value == null) return undefined;
  const row = objectValue(value), fallback = localizedText(row), entries = Object.entries(objectValue(row.locales));
  const defaultLocale = stringValue(row.defaultLocale);
  if (!localeName.test(defaultLocale) || entries.length > 16) throw new TypeError('extension_locales_invalid');
  const locales = Object.fromEntries(entries.map(([name, value]) => {
    if (!localeName.test(name)) throw new TypeError('extension_locale_invalid');
    return [name, localizedText(value)];
  }));
  const selected = locales[defaultLocale];
  if (!selected || Object.keys(fallback).some(key => fallback[key as keyof ExtensionText] !== selected[key as keyof ExtensionText]))
    throw new TypeError('extension_locale_unbound');
  const tags = arrayValue(row.tags, value => text(value, 80, true));
  if (tags.length > 32 || new Set(tags).size !== tags.length) throw new TypeError('extension_tags_invalid');
  return { ...fallback, defaultLocale, locales, tags,
    ...(row.author == null ? {} : { author: text(row.author, 120) }),
    createdAt: row.createdAt == null ? null : dateValue(row.createdAt), updatedAt: row.updatedAt == null ? null : dateValue(row.updatedAt) };
}
export function metadataText(metadata: ExtensionMetadata, locale: string): ExtensionText {
  return metadata.locales[locale] || metadata.locales[metadata.defaultLocale] || metadata;
}
export function titleLocales(value: unknown): Readonly<Record<string, string>> | undefined {
  if (value == null) return undefined;
  const entries = Object.entries(objectValue(value));
  if (entries.length > 16) throw new TypeError('task_locales_invalid');
  return Object.fromEntries(entries.map(([name, value]) => {
    if (!localeName.test(name)) throw new TypeError('task_locale_invalid');
    return [name, text(value, 256, true)];
  }));
}
export function taskTitle(task: { title: string; titleLocales?: Readonly<Record<string, string>> }, locale: string): string {
  return task.titleLocales?.[locale] || task.title;
}
export function diagnosticLocales(value: unknown): Readonly<Record<string, Readonly<Record<string, string>>>> {
  if (value == null) return {};
  const entries = Object.entries(objectValue(value));
  if (entries.length > 16) throw new TypeError('diagnostic_locales_invalid');
  return Object.fromEntries(entries.map(([name, value]) => {
    const messages = Object.entries(objectValue(value));
    if (!localeName.test(name) || messages.length > 4096) throw new TypeError('diagnostic_locale_invalid');
    return [name, Object.fromEntries(messages.map(([code, value]) => {
      if (!code || code.length > 120 || /[\u0000-\u001f]/.test(code)) throw new TypeError('diagnostic_code_invalid');
      return [code, text(value, 2048, true)];
    }))];
  }));
}
