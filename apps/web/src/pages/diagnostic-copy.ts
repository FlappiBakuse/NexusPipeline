import { locale } from '../app/locale';

const copy: Record<string, [string, string]> = {
  'host.version': ['Host 版本', 'Host version'],
  'host.admin-integrity': ['管理员权限', 'Administrator privileges'],
  'host.install-write': ['安装目录权限', 'Installation directory access'],
  'web.listener': ['Web 监听', 'Web listener'],
  'mcp.listener': ['MCP 监听', 'MCP listener'],
  'recovery.state': ['资源拥有权与恢复', 'Resource ownership and recovery'],
  'update.transaction': ['更新事务', 'Update transactions'],
  'config.recovery': ['配置编辑与恢复', 'Configuration editing and recovery'],
  'execution.state': ['执行状态', 'Execution state'],
  'scheduler.state': ['调度状态', 'Scheduler state'],
  'plugin.pending': ['插件待处理操作', 'Pending extension operations'],
  'plugin.runtime': ['插件运行状态', 'Extension runtime state'],
  'dependency.python': ['Python 判定依赖', 'Python judgement dependency'],
  'dependency.adb': ['Android 工具依赖', 'Android tool dependency'],
  'logs.recent': ['安全日志尾', 'Safe log tail'],
  'host.current': ['当前 Host 0.18.0 / SDK 0.1.0', 'Current Host 0.18.0 / SDK 0.1.0'],
  'host.admin': ['生产 Host 需要管理员权限；测试 Host 可使用普通权限。', 'Production Host requires administrator privileges; Test Host can run without elevation.'],
  'install.permission': ['检查安装目录的创建、写入和删除权限；失败时检查目录权限及占用。', 'Checks create, write and delete access in the installation directory; inspect access and locks on failure.'],
  'listener.bound': ['当前已绑定端口', 'Currently bound port'],
  'listener.disabled': ['当前未启用', 'Currently disabled'],
  'recovery.inspect': ['非就绪时检查恢复与隔离状态；不要删除原始数据。', 'Inspect recovery and isolation when blocked; preserve original data.'],
  'update.inspect': ['存在待处理更新或更新循环异常时检查更新与维护操作。', 'Inspect update and maintenance operations when pending or unhealthy.'],
  'config.inspect': ['存在未完成配置编辑；先返回所属编辑器保存或取消。', 'Unfinished configuration editors must be saved or canceled by their owner.'],
  'execution.inspect': ['检查受理操作及执行恢复状态；未知外部副作用不自动重放。', 'Inspect admitted operations and execution recovery; unknown effects are not replayed.'],
  'scheduler.inspect': ['检查调度循环及等待发生项。', 'Inspect the scheduling loop and waiting occurrences.'],
  'plugin.recover': ['待处理插件事务由插件恢复流程处理。', 'Pending extension transactions belong to extension recovery.'],
  'plugin.inspect': ['启用插件应处于活动状态；异常时检查插件中心。', 'Enabled extensions should be active; inspect the extension center on failure.'],
  'python.available': ['按当前 Python 判定需求检查 PATH 中的解释器文件；此检查不执行解释器。', 'Checks the interpreter file on PATH when Python judgement is used; does not run it.'],
  'adb.selected': ['检查 Android 目标所选工具文件；不依赖全局 ADB，不连接设备或启动脚本。', 'Checks tools selected by Android targets; does not require global ADB or connect to devices.'],
  'logs.typed': ['最多 64 条结构化完成事件；不导出工作进程原文、路径或配置。', 'At most 64 typed completion events; excludes worker text, paths and configuration.'],
  pass: ['正常', 'Passed'], warn: ['需关注', 'Needs attention'], fail: ['失败', 'Failed'],
  notRequired: ['按条件跳过', 'Not required'], notRun: ['未验证', 'Not verified']
};

export function diagnosticCopy(key: string): string {
  const value = copy[key];
  if (!value) throw new Error('diagnostic_code_invalid');
  return value[locale.value === 'en-US' ? 1 : 0];
}
