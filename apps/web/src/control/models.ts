import type { AutomationBundle, ControlHostSettingsValues } from './wire.generated';
import { arrayValue, integerValue, objectValue, stringValue } from './client';
import { automationBundle } from './automation';
import { settingsValues } from './settings';

export interface Capabilities {
  instanceId: string;
  capabilities: string[];
  hostVersion: string;
}
export interface AutomationSnapshot {
  catalogRevision: number;
  bundle: AutomationBundle;
  roots: { id: string; revision: number; path: string }[];
  revisions: { kind: string; id: string; revision: number }[];
}
export interface SettingsSnapshot {
  saved: { revision: number; values: ControlHostSettingsValues };
  effective: ControlHostSettingsValues;
  restartReasons: string[];
  startupEffect?: { operationId: string; settingsRevision: number; enable: boolean; state: string } | null;
}
export interface HostStatus {
  instanceId: string;
  processId: number;
  hostVersion: string;
  admission: unknown;
  currentWebPort: number | null;
  pendingOperations: number;
  execution: Record<string, unknown>;
  scheduling: Record<string, unknown>;
  accessAddresses?: { lan: string[]; public: string[] };
}
export type { RecordRow, RecordPage } from './records';
export { recordPage } from './records';

export function capabilities(value: unknown): Capabilities {
  const row = objectValue(value);
  return {
    instanceId: stringValue(row.instanceId),
    hostVersion: stringValue(row.hostVersion),
    capabilities: arrayValue(row.capabilities, stringValue)
  };
}
export function automation(value: unknown): AutomationSnapshot {
  const row = objectValue(value);
  const bundle = automationBundle(row.bundle);
  const roots = arrayValue(row.roots, (item) => {
    const root = objectValue(item);
    return { id: stringValue(root.id), revision: integerValue(root.revision), path: stringValue(root.path) };
  });
  const revisions = arrayValue(row.revisions, (item) => {
    const revision = objectValue(item);
    return {
      kind: stringValue(revision.kind),
      id: stringValue(revision.id),
      revision: integerValue(revision.revision)
    };
  });
  return {
    catalogRevision: integerValue(row.catalogRevision),
    bundle,
    roots,
    revisions
  };
}
export function settings(value: unknown): SettingsSnapshot {
  const row = objectValue(value);
  const saved = objectValue(row.saved);
  const values = settingsValues(saved.values);
  const effect = row.startupEffect == null ? null : objectValue(row.startupEffect);
  if (
    effect &&
    (typeof effect.enable !== 'boolean' ||
      !['prepared', 'entered', 'completed', 'quarantined'].includes(stringValue(effect.state)))
  )
    throw new TypeError('startup_effect_invalid');
  return {
    saved: { revision: integerValue(saved.revision), values },
    effective: settingsValues(row.effective),
    restartReasons: arrayValue(row.restartReasons, stringValue),
    startupEffect: effect
      ? {
          operationId: stringValue(effect.operationId),
          settingsRevision: integerValue(effect.settingsRevision),
          enable: effect.enable as boolean,
          state: stringValue(effect.state)
        }
      : null
  };
}
export function status(value: unknown): HostStatus {
  const row = objectValue(value);
  const addresses = row.accessAddresses == null ? undefined : objectValue(row.accessAddresses);
  return {
    instanceId: stringValue(row.instanceId),
    processId: integerValue(row.processId),
    hostVersion: stringValue(row.hostVersion),
    admission: row.admission,
    currentWebPort: row.currentWebPort == null ? null : integerValue(row.currentWebPort),
    pendingOperations: integerValue(row.pendingOperations),
    execution: objectValue(row.execution),
    scheduling: objectValue(row.scheduling),
    ...(addresses
      ? {
          accessAddresses: {
            lan: arrayValue(addresses.lan, stringValue),
            public: arrayValue(addresses.public, stringValue)
          }
        }
      : {})
  };
}
