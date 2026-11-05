import type { ControlHostSettingsValues } from './wire.generated';
import { arrayValue, booleanValue, boundedInteger, enumValue, objectValue, stringValue } from './client';

export function settingsValues(value: unknown): ControlHostSettingsValues {
  const row = objectValue(value), desktop = objectValue(row.desktop), service = objectValue(row.service),
    proxy = objectValue(row.proxy), updates = objectValue(row.updates), retry = objectValue(row.automaticRetry);
  const nullable = (value: unknown) => value === null ? null : stringValue(value);
  const webPort = boundedInteger(service.webPort, 0, 65535);
  if (webPort > 0 && webPort < 1024) throw new TypeError('settings_web_port_invalid');
  return {
    autoStart: booleanValue(row.autoStart), allowConfigRepair: booleanValue(row.allowConfigRepair),
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
