import type { TaskDisplaySnapshot, TaskTextRef } from './taskTypes';

// Resolve only frozen plain text. Neither task IDs nor installed plugin assets participate.
export function resolveTaskText(ref: TaskTextRef | undefined, snapshot: TaskDisplaySnapshot | undefined, locale: string, fallback: string): string {
  if (!ref) return fallback;
  if (ref.kind === 'literal') return ref.value;
  function format(template: string): string | undefined {
    let valid = true;
    const value = template.replace(/\{([A-Za-z0-9_.-]+)\}/g, (_, key: string) => {
      if (!Object.hasOwn(ref!.kind === 'plugin' ? ref!.args : {}, key)) { valid = false; return ''; }
      return String(ref!.kind === 'plugin' ? ref!.args[key] : '');
    });
    return valid && value.length <= 4096 ? value : undefined;
  }
  if (snapshot) {
    const normalized = locale.replaceAll('_', '-').toLowerCase();
    const locales = Object.keys(snapshot.messages).sort();
    const candidates = [...locales.filter(l => l.toLowerCase() === normalized),
      ...locales.filter(l => l.toLowerCase().split('-')[0] === normalized.split('-')[0]), snapshot.defaultLocale];
    for (const candidate of new Set(candidates)) {
      const messages = snapshot.messages[candidate];
      const template = messages && Object.hasOwn(messages, ref.key) ? messages[ref.key] : undefined;
      if (template !== undefined) { const value = format(template); if (value !== undefined) return value; }
    }
  }
  return format(ref.fallback) ?? fallback;
}
