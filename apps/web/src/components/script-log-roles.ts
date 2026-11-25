export const scriptLogRoles = ['preHook', 'main', 'postHook'] as const;
export type ScriptLogRole = typeof scriptLogRoles[number];
export type ScriptLogSelection = 'all' | ScriptLogRole;
export const scriptLogRoleLabels = { all: '全部', preHook: '前置', main: '主任务', postHook: '后置' } as const;

export function needsLogSeparator(previous: string, next: string): boolean {
  return previous.length > 0 && next.length > 0 && !previous.endsWith('\n') && !next.startsWith('\n');
}
