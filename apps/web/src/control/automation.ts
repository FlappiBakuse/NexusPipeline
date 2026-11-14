import type { AutomationBundle, AutomationTarget, AutomationBindingPolicyOverrides,
  AutomationPathsPortablePath, ConfigurationSelectorAccess } from './wire.generated';
import { arrayValue, booleanValue, boundedInteger, dateValue, enumValue, integerValue, objectValue, stringValue } from './client';

type Script = AutomationBundle['scripts'][number];
type Binding = AutomationBundle['bindings'][number];
const strings = (value: unknown) => arrayValue(value, stringValue);
const optional = <T>(row: Record<string, unknown>, key: string, decode: (value: unknown) => T): { [key: string]: T } =>
  row[key] === undefined ? {} : { [key]: decode(row[key]) };
const days = (value: unknown) => boundedInteger(value, -1, 36500);
function daily(value: unknown): number {
  const result = boundedInteger(value, -1, 9999);
  if (result === 0) throw new TypeError('automation_daily_limit_invalid');
  return result;
}
export function portablePath(value: unknown): AutomationPathsPortablePath {
  const row = objectValue(value);
  return { rootId: stringValue(row.rootId), relative: stringValue(row.relative) };
}
function hook(value: unknown) {
  const row = objectValue(value);
  return { executable: portablePath(row.executable), arguments: strings(row.arguments) };
}
function advanced(row: Record<string, unknown>) {
  return { ...optional(row, 'preHook', hook), ...optional(row, 'postHook', hook),
    ...optional(row, 'preOnceAfterSuccess', booleanValue), ...optional(row, 'postFinalOnly', booleanValue) };
}
function overrides(value: unknown): AutomationBindingPolicyOverrides {
  const row = objectValue(value), general = objectValue(row.general), notification = objectValue(row.notification),
    extra = objectValue(row.advanced);
  return {
    general: { syncEnabled: booleanValue(general.syncEnabled), enabled: booleanValue(general.enabled),
      runDays: days(general.runDays), maximumSuccessfulRunsPerDay: daily(general.maximumSuccessfulRunsPerDay) },
    notification: { syncEnabled: booleanValue(notification.syncEnabled), notifyEnabled: booleanValue(notification.notifyEnabled),
      notificationTaskIds: strings(notification.notificationTaskIds) },
    advanced: { ...advanced(extra), syncEnabled: booleanValue(extra.syncEnabled),
      preOnceAfterSuccess: booleanValue(extra.preOnceAfterSuccess), postFinalOnly: booleanValue(extra.postFinalOnly) }
  };
}
function target(value: unknown): AutomationTarget {
  const row = objectValue(value), kind = enumValue(row.kind, ['pc', 'android']);
  const common = { kind, launchByHost: booleanValue(row.launchByHost),
    waitSeconds: boundedInteger(row.waitSeconds, 0, 604800), forceCloseAtEnd: booleanValue(row.forceCloseAtEnd) };
  if (kind === 'pc') {
    if (row.android !== undefined) throw new TypeError('automation_target_invalid');
    const pc = objectValue(row.pc);
    return { ...common, pc: { launchExecutable: portablePath(pc.launchExecutable), arguments: strings(pc.arguments),
      readyExecutable: portablePath(pc.readyExecutable), readinessMode: enumValue(pc.readinessMode, ['process', 'visibleWindow']) } };
  }
  if (row.pc !== undefined) throw new TypeError('automation_target_invalid');
  const android = objectValue(row.android), driver = enumValue(android.driver, ['genericAdb', 'mumu']);
  if (driver === 'genericAdb' && android.screenshotTool !== undefined || driver === 'mumu' && android.emulator !== undefined)
    throw new TypeError('automation_target_invalid');
  return { ...common, android: { driver, tool: portablePath(android.tool), endpoint: stringValue(android.endpoint),
    package: stringValue(android.package), startArguments: strings(android.startArguments),
    ...optional(android, 'screenshotTool', portablePath), ...optional(android, 'emulator', value => {
      const emulator = objectValue(value);
      return { extensionId: stringValue(emulator.extensionId), installationId: stringValue(emulator.installationId),
        instanceId: stringValue(emulator.instanceId) };
    }) } };
}
function selector(value: unknown): unknown[] {
  return arrayValue(value, step => {
    if (typeof step === 'string') return step;
    if (typeof step === 'number') return integerValue(step);
    const match = objectValue(objectValue(step).match);
    return { match: { property: stringValue(match.property), value: stringValue(match.value) } };
  });
}
function access(value: unknown): ConfigurationSelectorAccess {
  const row = objectValue(value);
  const grant = (value: unknown) => {
    const row = objectValue(value);
    return { selector: selector(row.selector), descendants: booleanValue(row.descendants) };
  };
  return { read: arrayValue(row.read, grant), write: arrayValue(row.write, grant), protected: arrayValue(row.protected, selector) };
}
function script(value: unknown): Script {
  const row = objectValue(value), limits = objectValue(row.limits), judgement = objectValue(row.judgement);
  const kind = enumValue(row.kind, ['generic', 'specialized']);
  const updateConfigurationAtFinish = booleanValue(row.updateConfigurationAtFinish);
  if (kind === 'generic' && row.extensionId !== undefined || kind === 'specialized' && !updateConfigurationAtFinish)
    throw new TypeError('automation_script_invalid');
  return { id: stringValue(row.id), name: stringValue(row.name), kind, rootId: stringValue(row.rootId),
    ...(kind === 'specialized' ? { extensionId: stringValue(row.extensionId) } : {}),
    executable: portablePath(row.executable), arguments: strings(row.arguments),
    configurationPaths: arrayValue(row.configurationPaths, portablePath), logPaths: arrayValue(row.logPaths, portablePath),
    limits: { maximumAttempts: boundedInteger(limits.maximumAttempts, 1, 99),
      stallTimeoutSeconds: boundedInteger(limits.stallTimeoutSeconds, 0, 86400),
      totalTimeoutSeconds: boundedInteger(limits.totalTimeoutSeconds, 0, 604800) },
    judgement: { successKeywords: strings(judgement.successKeywords), failureKeywords: strings(judgement.failureKeywords),
      language: enumValue(judgement.language, ['keywords', 'javascript', 'python']), ...optional(judgement, 'script', stringValue) },
    updateConfigurationAtFinish, ...optional(row, 'target', target), ...optional(row, 'order', integerValue),
    adapterResources: arrayValue(row.adapterResources, value => {
      const resource = objectValue(value);
      return { resourceId: stringValue(resource.resourceId), declarationIndex: boundedInteger(resource.declarationIndex, 0, 31),
        relativePath: stringValue(resource.relativePath), definitionRevision: boundedInteger(resource.definitionRevision, 1, Number.MAX_SAFE_INTEGER),
        syntax: enumValue(resource.syntax, ['opaque', 'json', 'jsonWithComments', 'yaml']), access: access(resource.access) };
    }) };
}
function binding(value: unknown): Binding {
  const row = objectValue(value), extra = advanced(row);
  if (extra.preOnceAfterSuccess && !extra.preHook || extra.postFinalOnly && !extra.postHook)
    throw new TypeError('automation_hook_invalid');
  return { id: stringValue(row.id), userId: stringValue(row.userId), scriptId: stringValue(row.scriptId),
    enabled: booleanValue(row.enabled), priority: integerValue(row.priority), notifyEnabled: booleanValue(row.notifyEnabled),
    notificationTaskIds: strings(row.notificationTaskIds), ...extra, ...optional(row, 'runDays', days),
    ...optional(row, 'maximumSuccessfulRunsPerDay', daily) };
}
export function automationBundle(value: unknown): AutomationBundle {
  const row = objectValue(value);
  if (row.format !== 'nexus.automation.bundle' || row.version !== 1) throw new TypeError('automation_format_invalid');
  return { format: 'nexus.automation.bundle', version: 1, bundleId: stringValue(row.bundleId), createdAt: dateValue(row.createdAt),
    exportKind: enumValue(row.exportKind, ['definitions', 'scriptTemplate']),
    roots: arrayValue(row.roots, value => {
      const root = objectValue(value);
      return { id: stringValue(root.id), name: stringValue(root.name), kind: enumValue(root.kind, ['script', 'external']) };
    }), scripts: arrayValue(row.scripts, script), bindings: arrayValue(row.bindings, binding),
    users: arrayValue(row.users, value => {
      const user = objectValue(value);
      return { id: stringValue(user.id), name: stringValue(user.name), order: integerValue(user.order),
        ...optional(user, 'notes', stringValue), ...optional(user, 'avatar', stringValue), ...optional(user, 'bindingOverrides', overrides) };
    }), queues: arrayValue(row.queues, value => {
      const queue = objectValue(value);
      return { id: stringValue(queue.id), name: stringValue(queue.name), order: integerValue(queue.order),
        scriptIds: strings(queue.scriptIds), completionAction: enumValue(queue.completionAction, ['none', 'shutdown', 'hibernate', 'restart', 'exit']),
        notifyEnabled: booleanValue(queue.notifyEnabled), notificationTaskIds: strings(queue.notificationTaskIds),
        ...optional(queue, 'runOnStartup', booleanValue), ...optional(queue, 'scheduleEnabled', booleanValue),
        ...optional(queue, 'successDependencies', value => arrayValue(value, value => {
          const dependency = objectValue(value);
          return { scriptId: stringValue(dependency.scriptId), requiredScriptIds: strings(dependency.requiredScriptIds) };
        })) };
    }), schedules: arrayValue(row.schedules, value => {
      const schedule = objectValue(value);
      if (schedule.missedPolicy !== 'skip') throw new TypeError('automation_schedule_invalid');
      return { id: stringValue(schedule.id), queueId: stringValue(schedule.queueId), enabled: booleanValue(schedule.enabled),
        weekdays: arrayValue(schedule.weekdays, value => boundedInteger(value, 0, 6)), localMinute: boundedInteger(schedule.localMinute, 0, 1439),
        timeZoneId: stringValue(schedule.timeZoneId), missedPolicy: 'skip' as const, ...optional(schedule, 'order', integerValue) };
    }), attachments: arrayValue(row.attachments, value => {
      const attachment = objectValue(value);
      return { id: stringValue(attachment.id), path: stringValue(attachment.path), sha256: stringValue(attachment.sha256),
        sizeBytes: boundedInteger(attachment.sizeBytes, 0, 8388608) };
    }) };
}

