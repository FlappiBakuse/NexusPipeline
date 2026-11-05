export type PageId =
  | 'dashboard'
  | 'dispatch'
  | 'queues'
  | 'scripts'
  | 'users'
  | 'history'
  | 'plugins'
  | 'notifications'
  | 'settings';
export type AppRoute = PageId | `plugin/${string}`;

export const navigation = [
  ['dashboard', '今日概览', 'Overview', '◈'],
  ['dispatch', '运行中心', 'Run center', '▷'],
  ['queues', '计划与队列', 'Schedules & queues', '☷'],
  ['scripts', '脚本库', 'Scripts', '⌘'],
  ['users', '账号与配置', 'Accounts & configuration', '♙'],
  ['history', '运行历史', 'Run history', '◷'],
  ['plugins', '插件中心', 'Plugins', '◇'],
  ['notifications', '通知', 'Notifications', '♧'],
  ['settings', '设置', 'Settings', '⚙']
] as const;

export function routePage(): AppRoute {
  const path = location.hash.replace(/^#\//, '').split('?')[0] || location.pathname.slice(1);
  if (/^plugin\/[a-z0-9-]+(?:\/[a-z0-9-]+)*$/.test(path)) return path as AppRoute;
  return navigation.find((item) => item[0] === path)?.[0] || 'dashboard';
}
