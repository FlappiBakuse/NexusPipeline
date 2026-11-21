import { locale } from '../app/locale';

type Localized = readonly [string, string];
export const diagnosticStatuses = ['fail', 'warn', 'notRun', 'pass', 'notRequired'] as const;
export type DiagnosticStatus = typeof diagnosticStatuses[number];
export interface DiagnosticCheck { id: string; status: DiagnosticStatus; code: string; count: number | null }
export const diagnosticTones = { fail: 'danger', warn: 'warning', notRun: 'info', pass: 'success', notRequired: 'neutral' } as const;
const localize = (value: Localized): string => value[locale.value === 'en-US' ? 1 : 0];

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
  'host.admin': ['正式版和验收版需要管理员权限。普通测试构建使用调用方的权限。',
    'Release and acceptance builds require administrator privileges. Standard test builds use the caller’s privileges.'],
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
  return localize(value);
}

const findings: Record<string, { observation: Localized; action: Localized }> = {
  'host.admin-integrity': { observation: ['Host 当前使用普通权限', 'The Host is running without elevation'],
    action: ['使用正式版或验收版时，从资源管理器启动 NexusPipeline 并同意 Windows 权限请求，再刷新诊断。普通测试构建可使用普通权限。',
      'For a release or acceptance build, start NexusPipeline from File Explorer, approve the Windows prompt, then refresh. Standard test builds can run without elevation.'] },
  'host.install-write': { observation: ['安装目录写入检查失败', 'The installation directory write check failed'],
    action: ['检查安装目录的写入权限和文件占用，处理后刷新诊断。', 'Check write access and files in use in the installation directory, then refresh.'] },
  'recovery.state': { observation: ['当前恢复未就绪', 'Recovery is not ready'],
    action: ['等待宿主完成恢复后刷新诊断。若隔离持续，保留现有数据并导出脱敏诊断包。',
      'Refresh after the Host finishes recovery. If isolation persists, preserve the existing data and export a sanitized support bundle.'] },
  'update.transaction': { observation: ['更新流程需要检查', 'Updates need review'],
    action: ['等待正在进行的维护完成后刷新诊断。若提示持续，导出脱敏诊断包。',
      'Refresh after any ongoing maintenance finishes. Export a sanitized support bundle if the warning persists.'] },
  'config.recovery': { observation: ['有配置编辑尚未完成', 'A configuration edit is still open'],
    action: ['返回对应的配置编辑窗口，保存或取消编辑后刷新诊断。', 'Return to the configuration editor, save or cancel the edit, then refresh.'] },
  'execution.state': { observation: ['执行服务需要检查', 'Execution needs review'],
    action: ['在运行中心查看待核对的运行。问题持续时导出脱敏诊断包。', 'Review runs waiting for verification in the Run Center. Export a sanitized support bundle if the issue persists.'] },
  'scheduler.state': { observation: ['调度循环需要检查', 'Scheduling needs review'],
    action: ['在计划与队列中查看对应计划，刷新后若仍出现异常，导出脱敏诊断包。', 'Review the schedule in Plans and Queues. Export a sanitized support bundle if the issue persists after refreshing.'] },
  'plugin.pending': { observation: ['有插件操作等待恢复', 'An extension operation awaits recovery'],
    action: ['等待宿主完成插件恢复，再刷新诊断。若仍有待处理操作，导出脱敏诊断包。', 'Refresh after the Host finishes extension recovery. Export a sanitized support bundle if operations remain pending.'] },
  'plugin.runtime': { observation: ['有启用的插件未激活或遇到错误', 'An enabled extension is inactive or reported a failure'],
    action: ['打开插件中心，检查已启用插件的状态，处理后刷新诊断。', 'Check enabled extensions in the Extension Center, then refresh.'] },
  'dependency.python': { observation: ['未找到 Python 解释器文件', 'No Python interpreter file was found'],
    action: ['核对 Host 环境的 PATH 是否包含预期的 Python 解释器，再刷新诊断。', 'Check that the Host PATH contains the intended Python interpreter, then refresh.'] },
  'dependency.adb': { observation: ['部分 Android 目标的工具文件缺失', 'Some Android targets are missing tool files'],
    action: ['在对应脚本实例的 Android 目标设置中核对所选工具和截图工具，再刷新诊断。', 'Check the selected Android and capture tools in the affected script’s target settings, then refresh.'] }
};

const countLabels: Record<string, Localized> = {
  'web.listener': ['端口 {n}', 'Port {n}'], 'mcp.listener': ['端口 {n}', 'Port {n}'],
  'recovery.state': ['{n} 项隔离或持有资源', '{n} isolated or held resources'],
  'update.transaction': ['{n} 项待处理更新', '{n} pending updates'],
  'config.recovery': ['{n} 个未完成编辑', '{n} open edits'],
  'execution.state': ['{n} 项待确认操作', '{n} pending operations'],
  'scheduler.state': ['{n} 项等待调度', '{n} waiting occurrences'],
  'plugin.pending': ['{n} 项待恢复操作', '{n} operations awaiting recovery'],
  'plugin.runtime': ['{n} 个异常插件', '{n} unhealthy extensions'],
  'dependency.python': ['{n} 个 Python 判定脚本', '{n} scripts with Python judgement'],
  'dependency.adb': ['{n} 个目标缺少工具', '{n} targets missing tools'],
  'logs.recent': ['{n} 条完成事件', '{n} completion events']
};

const passed: Record<string, Localized> = {
  'host.admin-integrity': ['当前以管理员身份运行', 'Running as administrator'],
  'host.install-write': ['安装目录可写', 'The installation directory is writable'],
  'recovery.state': ['恢复就绪', 'Recovery is ready'],
  'update.transaction': ['没有待处理更新，更新循环正常', 'No updates are pending and the update loop is healthy'],
  'config.recovery': ['没有未完成的配置编辑', 'No configuration edits are open'],
  'execution.state': ['执行服务正常', 'The execution service is healthy'],
  'scheduler.state': ['调度循环正常', 'The scheduling loop is healthy'],
  'plugin.pending': ['没有待恢复的插件操作', 'No extension operations await recovery'],
  'plugin.runtime': ['已启用的插件无异常', 'Enabled extensions are healthy'],
  'dependency.python': ['已找到 Python 解释器文件', 'A Python interpreter file was found'],
  'dependency.adb': ['所选 Android 工具文件均存在', 'All selected Android tool files were found'],
  'logs.recent': ['已记录结构化完成事件', 'Structured completion events were recorded']
};

const notRequired: Record<string, Localized> = {
  'plugin.runtime': ['未安装插件', 'No extensions are installed'],
  'dependency.python': ['没有使用 Python 判定的脚本', 'No scripts use Python judgement'],
  'dependency.adb': ['没有配置 Android 目标', 'No Android targets are configured'],
  'logs.recent': ['没有可供检查的完成事件', 'There are no completion events to inspect']
};

const checkScope: Record<string, Localized> = {
  'host.admin-integrity': ['检查 Windows 当前进程令牌的管理员角色。', 'Checks the administrator role in the current Windows process token.'],
  'host.install-write': ['探针检查安装目录中的创建、写入、同步和删除。', 'The probe checks file creation, writing, flushing and deletion in the installation directory.'],
  'web.listener': ['读取 Host 实际绑定的 Web 监听端口。', 'Reports the Web listener port bound by the Host.'],
  'mcp.listener': ['启用 MCP 时读取 Host 实际绑定的端口。', 'Reports the port bound by the Host when MCP is enabled.'],
  'recovery.state': ['核对恢复准入与隔离、持有资源计数。', 'Checks recovery admission and the isolated and held resource counts.'],
  'update.transaction': ['核对未完成的维护事务和自动更新循环。', 'Checks pending maintenance transactions and the automatic update loop.'],
  'config.recovery': ['统计尚未结清的配置编辑会话。', 'Counts configuration edits that have not been settled.'],
  'execution.state': ['核对执行服务状态和待确认操作。', 'Checks the execution service and operations awaiting confirmation.'],
  'scheduler.state': ['核对调度循环和等待中的调度项。', 'Checks the scheduler loop and waiting occurrences.'],
  'plugin.pending': ['统计等待宿主恢复的插件事务。', 'Counts extension transactions awaiting Host recovery.'],
  'plugin.runtime': ['核对插件的启用设置、实际活动状态和故障记录。', 'Checks enablement, active status and recorded failures for extensions.'],
  'dependency.python': ['使用 Python 判定时，在 Host 的 PATH 中查找解释器文件。', 'Looks for an interpreter file on the Host PATH when Python judgement is used.'],
  'dependency.adb': ['配置 Android 目标时，核对该目标所选工具和截图工具文件。', 'Checks the selected tool and capture tool files for configured Android targets.'],
  'logs.recent': ['读取最近最多 64 条结构化运行完成事件。', 'Reads up to 64 recent structured run completion events.']
};

export function diagnosticPresentation(check: DiagnosticCheck, admission: string) {
  const { id, status, code, count } = check;
  const detail = diagnosticCopy(code);
  const finding = status === 'fail' || status === 'warn' ? findings[id] : undefined;
  const unknown = status === 'notRun';
  const changedDuringRead = id === 'recovery.state' && status === 'fail' && admission === 'changedDuringRead';
  const countLabel = !unknown && !changedDuringRead && count != null && count > 0 ? countLabels[id] : undefined;
  const result = status === 'pass' ? passed[id] : status === 'notRequired' ? notRequired[id] : finding?.observation;
  const observation = changedDuringRead ? localize(['读取期间恢复状态发生变化', 'Recovery changed while reading the report'])
    : unknown ? localize(['本次未取得检查结果', 'This check did not produce a result'])
    : result ? localize(result) : detail;
  const action = changedDuringRead ? localize(['刷新诊断，重新读取当前恢复状态后再判断。', 'Refresh to read the current recovery state again before taking action.'])
    : unknown ? localize(id === 'plugin.runtime'
    ? ['先刷新诊断。若仍未验证，打开插件中心查看已启用插件的状态。', 'Refresh first. If the check is still unverified, check the enabled extensions in the Extension Center.']
    : ['刷新诊断。若仍未验证，导出脱敏诊断包以便核查。', 'Refresh. If the check is still unverified, export a sanitized support bundle for review.'])
    : finding ? localize(finding.action) : '';
  const scope = checkScope[id];
  return { title: diagnosticCopy(id), observation, action, scope: scope ? localize(scope) : '',
    count: countLabel ? localize(countLabel).replace('{n}', String(count)) : '' };
}
