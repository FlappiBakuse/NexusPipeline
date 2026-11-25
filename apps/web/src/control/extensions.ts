import { arrayValue, integerValue, objectValue, stringValue } from './client';
import type { ExtensionManifest } from './wire.generated';
import { extensionMetadata, diagnosticLocales, type ExtensionMetadata } from './extension-metadata';

export interface ExtensionState {
  extensionId: string;
  installedRevision: number;
  isInstalled: boolean;
  desiredEnabled: boolean;
  activationId: string | null;
  unavailableReason: string | null;
  packageVersion?: string;
  kind?: ExtensionManifest['kind'];
  artifactName?: string;
  isUnofficial?: boolean;
  capabilities: string[];
  metadata?: ExtensionMetadata;
}
export interface CatalogIdentity {
  channel: 'stable' | 'develop';
  generationId: string;
  sequence: number;
  sha256: string;
}
export interface CatalogEntry {
  extensionId: string;
  artifactName: string;
  packageVersion: string;
  kind: ExtensionManifest['kind'];
  minHostVersion: string;
  sdkVersion: string;
  artifactUrl: string;
  artifactBytes: number;
  artifactSha256: string;
  manifestSha256: string;
  metadata?: ExtensionMetadata;
}
export interface ExtensionCatalog {
  identity: CatalogIdentity;
  publishedAt: string;
  entries: CatalogEntry[];
}
export interface ExtensionDetails {
  extensionId: string;
  packageVersion: string;
  manifest: ExtensionManifest;
  documents: Record<string, string>;
  installedRevision?: number;
  artifactSha256?: string;
  metadata?: ExtensionMetadata;
  diagnosticLocales: Readonly<Record<string, Readonly<Record<string, string>>>>;
}
function booleanValue(value: unknown): boolean {
  if (typeof value !== 'boolean') throw new TypeError('extension_boolean_invalid');
  return value;
}
function kind(value: unknown): ExtensionManifest['kind'] {
  if (value !== 'managed-code' && value !== 'data-specialized') throw new TypeError('extension_kind_invalid');
  return value;
}
export function extensionState(value: unknown): ExtensionState {
  const row = objectValue(value);
  return {
    extensionId: stringValue(row.extensionId),
    installedRevision: integerValue(row.installedRevision),
    isInstalled: booleanValue(row.isInstalled),
    desiredEnabled: booleanValue(row.desiredEnabled),
    activationId: row.activationId == null ? null : stringValue(row.activationId),
    unavailableReason: row.unavailableReason == null ? null : stringValue(row.unavailableReason),
    ...(row.packageVersion == null ? {} : { packageVersion: stringValue(row.packageVersion) }),
    ...(row.kind == null ? {} : { kind: kind(row.kind) }),
    ...(row.artifactName == null ? {} : { artifactName: stringValue(row.artifactName) }),
    ...(row.isUnofficial == null ? {} : { isUnofficial: booleanValue(row.isUnofficial) }),
    capabilities: arrayValue(row.capabilities, stringValue), metadata: extensionMetadata(row.metadata)
  };
}
export type ExtensionDirectory = { hasReleaseTrust: boolean; items: ExtensionState[] };

export interface LocalExtensionCandidate {
  candidateId: string;
  discoveryId: string;
  extensionId: string;
  name: string;
  summary: string;
  gameName?: string;
  kind: ExtensionManifest['kind'];
  packageVersion: string;
  minHostVersion: string;
  relativePath: string;
  capabilities: string[];
  state: 'pendingApproval' | 'pendingUpdate' | 'awaitingVerification' | 'unavailable';
  reason: string | null;
}

export interface LocalExtensionSnapshot {
  items: LocalExtensionCandidate[];
  scanning: boolean;
  notice: { batchId: string; removed: string[]; waiting: string[]; installed: string[] } | null;
}

export function localExtensionSnapshot(value: unknown): LocalExtensionSnapshot {
  const row = objectValue(value);
  const items = arrayValue(row.items, (value): LocalExtensionCandidate => {
    const item = objectValue(value);
    const candidateId = stringValue(item.candidateId), discoveryId = stringValue(item.discoveryId);
    const state = item.state;
    if (!/^[a-f0-9]{64}$/i.test(candidateId) || !/^[a-f0-9]{64}$/i.test(discoveryId) ||
      (state !== 'pendingApproval' && state !== 'pendingUpdate' && state !== 'awaitingVerification' && state !== 'unavailable'))
      throw new TypeError('extension_local_candidate_invalid');
    return {
      candidateId, discoveryId, state, extensionId: stringValue(item.extensionId), name: stringValue(item.name),
      summary: stringValue(item.summary), kind: kind(item.kind), packageVersion: stringValue(item.packageVersion),
      ...(item.gameName == null ? {} : { gameName: stringValue(item.gameName) }),
      minHostVersion: stringValue(item.minHostVersion), relativePath: stringValue(item.relativePath),
      capabilities: arrayValue(item.capabilities, stringValue), reason: item.reason == null ? null : stringValue(item.reason)
    };
  });
  if (items.length > 256 || new Set(items.map(item => item.candidateId)).size !== items.length)
    throw new TypeError('extension_local_snapshot_invalid');
  const notice = row.notice == null ? null : objectValue(row.notice);
  return { items, scanning: booleanValue(row.scanning), notice: notice && {
    batchId: stringValue(notice.batchId), removed: arrayValue(notice.removed, stringValue),
    waiting: arrayValue(notice.waiting, stringValue), installed: arrayValue(notice.installed, stringValue)
  } };
}

export function extensionList(value: unknown): ExtensionDirectory {
  const row = objectValue(value);
  const items = arrayValue(row.items, extensionState);
  if (items.length > 256 || new Set(items.map((item) => item.extensionId)).size !== items.length)
    throw new TypeError('extension_list_invalid');
  return { hasReleaseTrust: booleanValue(row.hasReleaseTrust), items };
}
export function extensionCatalog(value: unknown): ExtensionCatalog | null {
  if (value == null) return null;
  const row = objectValue(value),
    identity = objectValue(row.identity);
  if (!['stable', 'develop'].includes(String(identity.channel)))
    throw new TypeError('extension_channel_invalid');
  return {
    identity: {
      channel: identity.channel as CatalogIdentity['channel'],
      generationId: stringValue(identity.generationId),
      sequence: integerValue(identity.sequence),
      sha256: stringValue(identity.sha256)
    },
    publishedAt: stringValue(row.publishedAt),
    entries: arrayValue(row.entries, (item) => {
      const entry = objectValue(item);
      return {
        extensionId: stringValue(entry.extensionId),
        artifactName: stringValue(entry.artifactName),
        packageVersion: stringValue(entry.packageVersion),
        kind: kind(entry.kind),
        minHostVersion: stringValue(entry.minHostVersion),
        sdkVersion: stringValue(entry.sdkVersion),
        artifactUrl: stringValue(entry.artifactUrl),
        artifactBytes: integerValue(entry.artifactBytes),
        artifactSha256: stringValue(entry.artifactSha256),
        manifestSha256: stringValue(entry.manifestSha256), metadata: extensionMetadata(entry.metadata)
      };
    })
  };
}
export function extensionDetails(value: unknown, id: string): ExtensionDetails {
  const row = objectValue(value),
    manifest = objectValue(row.manifest),
    documents = objectValue(row.documents);
  if (
    row.extensionId !== id ||
    manifest.extensionId !== id ||
    manifest.format !== 'nexus.extension.manifest' ||
    manifest.version !== 1
  )
    throw new TypeError('extension_details_unbound');
  for (const field of ['displayName', 'artifactName', 'minHostVersion', 'packageVersion'])
    stringValue(manifest[field]);
  kind(manifest.kind);
  return {
    extensionId: id,
    packageVersion: stringValue(row.packageVersion),
    manifest: manifest as unknown as ExtensionManifest,
    metadata: extensionMetadata(manifest.metadata),
    diagnosticLocales: diagnosticLocales(row.diagnosticLocales),
    documents: Object.fromEntries(Object.entries(documents).map(([name, text]) => [name, stringValue(text)])),
    ...(row.installedRevision == null ? {} : { installedRevision: integerValue(row.installedRevision) }),
    ...(row.artifactSha256 == null ? {} : { artifactSha256: stringValue(row.artifactSha256) })
  };
}
export function releaseDocument(
  details: ExtensionDetails | undefined,
  prefix: 'README' | 'CHANGELOG',
  locale: string
): string {
  const docs = details?.documents || {};
  const names =
    locale === 'en-US'
      ? [prefix + '.en.md', prefix + '.md', prefix + '.zh-CN.md']
      : [prefix + '.zh-CN.md', prefix + '.md', prefix + '.en.md'];
  return names.map((name) => docs[name]).find((value) => value !== undefined) || '';
}
export function releaseSummary(details: ExtensionDetails | undefined, locale: string): string {
  return (
    releaseDocument(details, 'README', locale)
      .split(/\n\s*\n/)
      .find((text) => text.trim() && !/^\s*[#<>`|]/.test(text))
      ?.replace(/\[([^\]]+)\]\([^)]*\)/g, '$1')
      .replace(/[*_`]/g, '')
      .replace(/\s+/g, ' ')
      .trim() || ''
  );
}

export function versionOrder(left: string, right: string): number | null {
  const pattern = /^(\d+)\.(\d+)\.(\d+)(?:-(beta|rc)\.([1-9]\d*))?$/;
  const first = pattern.exec(left),
    second = pattern.exec(right);
  if (!first || !second) return null;
  for (const index of [1, 2, 3]) {
    const delta = Number(first[index]) - Number(second[index]);
    if (delta) return Math.sign(delta);
  }
  const stage = (value: string | undefined) => (value === 'beta' ? 0 : value === 'rc' ? 1 : 2);
  const delta = stage(first[4]) - stage(second[4]);
  return Math.sign(delta || Number(first[5] || 0) - Number(second[5] || 0));
}

export function upgradeAvailable(
  details: ExtensionDetails | undefined,
  candidate: CatalogEntry | undefined,
  channel: string
): boolean {
  if (!details?.artifactSha256 || !candidate || details.artifactSha256 === candidate.artifactSha256)
    return false;
  const order = versionOrder(candidate.packageVersion, details.packageVersion);
  return order !== null && (order > 0 || (order === 0 && channel === 'develop'));
}
