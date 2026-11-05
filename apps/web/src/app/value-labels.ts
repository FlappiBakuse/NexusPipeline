import { t, registerMessages } from './locale';

const labels: Record<string, string> = {
  none: '无',
  desktop: '桌面模式',
  browser: '浏览器模式',
  system: '跟随系统',
  zhCn: '简体中文',
  enUs: '英语',
  stable: '稳定版',
  prerelease: '预览版',
  develop: '开发版',
  trace: '跟踪',
  debug: '调试',
  info: '信息',
  warning: '警告',
  error: '错误',
  critical: '严重错误',
  manual: '手动',
  skip: '跳过',
  runOnce: '执行一次',
  relative: '相对路径',
  generic: '通用脚本',
  process: '本机进程',
  android: '安卓设备',
  adapter: '专项适配',
  shutdown: '关机',
  restart: '重启',
  sleep: '睡眠',
  hibernate: '休眠',
  lock: '锁定',
  logoff: '注销'
};
registerMessages({
  无: 'None',
  无操作: 'No action',
  桌面模式: 'Desktop',
  浏览器模式: 'Browser',
  跟随系统: 'System',
  简体中文: 'Simplified Chinese',
  英语: 'English',
  稳定版: 'Stable',
  预览版: 'Preview',
  开发版: 'Development',
  跟踪: 'Trace',
  严重错误: 'Critical',
  手动: 'Manual',
  跳过: 'Skip',
  执行一次: 'Run once',
  本机进程: 'Local process',
  安卓设备: 'Android device'
});
export function valueLabel(value: unknown, field?: string): string {
  const key = String(value);
  if (key === 'none') return t(field === 'completionAction' ? '无操作' : '无');
  return t(labels[key] || key);
}
