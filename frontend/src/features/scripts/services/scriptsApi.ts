import { api } from "../../../platform/api";

/** Scripts 域的请求封装。 */
export function listScripts(): Promise<any> {
  return api("GET", "/api/scripts");
}
export function getStatus(): Promise<any> {
  return api("GET", "/api/status");
}
export function createScript(payload: unknown): Promise<unknown> {
  return api("POST", "/api/scripts", payload);
}
export function updateScript(id: string, payload: unknown): Promise<unknown> {
  return api("PUT", `/api/scripts/${encodeURIComponent(id)}`, payload);
}
export interface ProviderConfigurationStatus {
  operationId: string;
  userId?: string;
  scriptId: string;
  state: string;
  revision: string;
  errorCode: string;
  cleanupConfirmed: boolean;
  progress: { code?: string };
}
export function saveProviderConfiguration(operationId: string, script: unknown, configuration: unknown): Promise<ProviderConfigurationStatus> {
  return api("POST", "/api/provider-configurations", { operationId, script, configuration });
}
export function getProviderConfiguration(operationId: string): Promise<ProviderConfigurationStatus> {
  return api("GET", `/api/provider-configurations/${encodeURIComponent(operationId)}`);
}
export function listProviderConfigurations(): Promise<ProviderConfigurationStatus[]> {
  return api("GET", "/api/provider-configurations");
}
export function cancelProviderConfiguration(operationId: string): Promise<unknown> {
  return api("POST", `/api/provider-configurations/${encodeURIComponent(operationId)}/cancel`, {});
}
export function deleteScript(id: string): Promise<unknown> {
  return api("DELETE", `/api/scripts/${encodeURIComponent(id)}`);
}
export function reorderScripts(ids: string[]): Promise<unknown> {
  return api("PUT", "/api/scripts/order", { ids });
}
export function probeScriptRoot(payload: { pluginType: string; rootPath: string; inputs: Record<string, unknown> }): Promise<unknown> {
  return api("POST", "/api/scripts/probe", payload);
}
export function browseNativeDialog(payload: {
  kind: "file" | "folder";
  title: string;
  initialPath?: string;
  filter?: string;
}): Promise<any> {
  return api("POST", "/api/native-dialog", payload);
}
