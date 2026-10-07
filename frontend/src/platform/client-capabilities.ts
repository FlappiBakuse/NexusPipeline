import { api, readAuthToken } from "./api";
import { onServiceTrafficChanged } from "./service-traffic";

export type ConnectionKind = "local" | "remote" | "unknown";
export type OperationAccess = "general" | "hostFilePicker" | "nativeConfigEditor";
export type AccessDenyReason = "host_file_picker_requires_local" | "native_config_editor_requires_local" | "client_origin_unverified";
export interface AccessDecision { readonly allowed: boolean; readonly denyReason: AccessDenyReason | null }
export interface ClientCapabilities {
  readonly schemaVersion: 1;
  readonly connectionKind: ConnectionKind;
  readonly operations: Readonly<Record<OperationAccess, AccessDecision>>;
}
let cached: Readonly<ClientCapabilities> | null = null;
let cacheOrigin = "", cacheAuth: string | null = null, generation = 0;
export function invalidateClientCapabilities(): void { generation++; cached = null; cacheAuth = null; }
onServiceTrafficChanged(paused => { if (paused) invalidateClientCapabilities(); });

function exact(value: unknown, keys: string[]): value is Record<string, unknown> {
  return Boolean(value && typeof value === "object" && !Array.isArray(value)
    && Object.keys(value).sort().join("|") === keys.sort().join("|"));
}
export function validateClientCapabilities(value: unknown): Readonly<ClientCapabilities> {
  if (!exact(value, ["schemaVersion", "connectionKind", "operations"]) || value.schemaVersion !== 1
    || !["local", "remote", "unknown"].includes(String(value.connectionKind))
    || !exact(value.operations, ["general", "hostFilePicker", "nativeConfigEditor"])) throw new Error("client_capabilities_schema_invalid");
  const connection = value.connectionKind as ConnectionKind;
  const operations = {} as Record<OperationAccess, AccessDecision>;
  for (const operation of ["general", "hostFilePicker", "nativeConfigEditor"] as OperationAccess[]) {
    const decision = value.operations[operation];
    const allowed = operation === "general" || connection === "local";
    const reason = allowed ? null : connection === "unknown" ? "client_origin_unverified"
      : operation === "hostFilePicker" ? "host_file_picker_requires_local" : "native_config_editor_requires_local";
    if (!exact(decision, ["allowed", "denyReason"]) || decision.allowed !== allowed || decision.denyReason !== reason)
      throw new Error("client_capabilities_schema_invalid");
    operations[operation] = Object.freeze({ allowed, denyReason: reason });
  }
  return Object.freeze({ schemaVersion: 1, connectionKind: connection, operations: Object.freeze(operations) });
}
export async function getCapabilities(signal?: AbortSignal): Promise<Readonly<ClientCapabilities>> {
  signal?.throwIfAborted();
  const origin = location.origin, auth = readAuthToken();
  if (cached && cacheOrigin === origin && cacheAuth === auth) return cached;
  const current = generation;
  const value = validateClientCapabilities(await api("GET", "/api/client-capabilities", undefined, signal));
  if (current !== generation || origin !== location.origin || auth !== readAuthToken()) throw new DOMException("Connection changed", "AbortError");
  cacheOrigin = origin; cacheAuth = auth; cached = value;
  return value;
}
