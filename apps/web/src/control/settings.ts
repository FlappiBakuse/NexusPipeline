import type { ControlHostSettingsValues, ControlHostSettings } from './wire.generated';
import { arrayValue, booleanValue, boundedInteger, enumValue, objectValue, stringValue, integerValue } from './client';

export function settingsValues(value: unknown): ControlHostSettingsValues {
  const row = objectValue(value), desktop = objectValue(row.desktop), service = objectValue(row.service),
    proxy = objectValue(row.proxy), updates = objectValue(row.updates), retry = objectValue(row.automaticRetry);
  const nullable = (value: unknown) => value === null ? null : stringValue(value);
  const webPort = boundedInteger(service.webPort, 0, 65535);
  if (webPort > 0 && webPort < 1024) throw new TypeError('settings_web_port_invalid');
  return {
    autoStart: booleanValue(row.autoStart),
    historyRetentionDays: boundedInteger(row.historyRetentionDays, 1, 180),
    logLevel: enumValue(row.logLevel, ['debug', 'info', 'warning', 'error', 'fatal']),
    locale: enumValue(row.locale, ['zhCn', 'enUs']),
    desktop: { mode: enumValue(desktop.mode, ['desktop', 'browser']), minimizeToTray: booleanValue(desktop.minimizeToTray),
      openOnStartup: booleanValue(desktop.openOnStartup) },
    service: { webPort, allowRemoteAccess: booleanValue(service.allowRemoteAccess), mcpEnabled: booleanValue(service.mcpEnabled),
      mcpWriteEnabled: booleanValue(service.mcpWriteEnabled), mcpPort: boundedInteger(service.mcpPort, 1024, 65535) },
    proxy: { mode: enumValue(proxy.mode, ['none', 'system', 'http']), url: nullable(proxy.url), userName: stringValue(proxy.userName) },
    updates: { checkEnabled: booleanValue(updates.checkEnabled), autoApplyEnabled: booleanValue(updates.autoApplyEnabled),
      pluginAutoUpdateEnabled: booleanValue(updates.pluginAutoUpdateEnabled), channel: enumValue(updates.channel, ['stable', 'prerelease']),
      sourceUrl: nullable(updates.sourceUrl), pluginSource: enumValue(updates.pluginSource, ['stable', 'develop']) },
    automaticRetry: { enabled: booleanValue(retry.enabled), backoffSeconds: boundedInteger(retry.backoffSeconds, 0, 86400) },
    extensions: arrayValue(row.extensions, value => {
      const extension = objectValue(value);
      return { extensionId: stringValue(extension.extensionId), enabled: booleanValue(extension.enabled) };
    })
  };
}

export function sameValue(left: unknown, right: unknown): boolean {
  if (left === right) return true;
  if (Array.isArray(left) || Array.isArray(right))
    return (
      Array.isArray(left) &&
      Array.isArray(right) &&
      left.length === right.length &&
      left.every((value, index) => sameValue(value, right[index]))
    );
  if (!left || !right || typeof left !== 'object' || typeof right !== 'object') return false;
  const first = left as Record<string, unknown>,
    second = right as Record<string, unknown>;
  const keys = Object.keys(first);
  return (
    keys.length === Object.keys(second).length &&
    keys.every((key) => Object.hasOwn(second, key) && sameValue(first[key], second[key]))
  );
}

export function savedSettings(value: unknown, body: ControlHostSettings): Record<string, unknown> {
  const response = objectValue(value),
    saved = objectValue(response.saved);
  if (
    response.operationId !== body.operationId ||
    integerValue(saved.revision) !== body.expectedRevision + 1 ||
    !sameValue(saved.values, body.values)
  )
    throw new TypeError('settings_result_unbound');
  return response;
}

export function completedSettings(value: unknown, body: ControlHostSettings): boolean {
  const operation = objectValue(value);
  return (
    operation.operationId === body.operationId &&
    operation.kind === 'settings.save' &&
    operation.status === 'completed'
  );
}
