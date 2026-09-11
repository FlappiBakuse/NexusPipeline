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
