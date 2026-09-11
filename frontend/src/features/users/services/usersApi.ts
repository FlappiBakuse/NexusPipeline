import { api } from "../../../platform/api";

/** Users 域的请求封装：页面与 feature 组件通过它访问用户、绑定、全局设置、
 *  插件贡献和配置编辑事务端点，避免 URL 拼接散落在各组件。 */

export interface UserBadgePayload {
  userId?: string;
  badges?: Array<{ pluginName?: string; id?: string; label?: string; tone?: string; title?: string }>;
}

export function listUsers(): Promise<any> {
  return api("GET", "/api/users");
}
export function listScripts(): Promise<any> {
  return api("GET", "/api/scripts");
}
export function getStatus(): Promise<any> {
  return api("GET", "/api/status");
}
export function listUserBadges(): Promise<any> {
  return api("GET", "/api/plugin-contributions/user-list-badges");
}
export function createUser(name: string): Promise<unknown> {
  return api("POST", "/api/users", { name });
}
export function updateUser(id: string, payload: { name: string; remark: string }): Promise<unknown> {
  return api("PUT", `/api/users/${encodeURIComponent(id)}`, payload);
}
export function deleteUser(id: string, confirmName: string): Promise<unknown> {
  return api("DELETE", `/api/users/${encodeURIComponent(id)}`, { confirmName });
}
export function reorderUsers(ids: string[]): Promise<unknown> {
  return api("PUT", "/api/users/order", { ids });
}
export function getUser(id: string): Promise<any> {
  return api("GET", `/api/users/${encodeURIComponent(id)}`);
}

export function getGlobalSettings(id: string): Promise<any> {
  return api("GET", `/api/users/${encodeURIComponent(id)}/global-settings`);
}
export function saveGlobalSettings(id: string, settings: unknown): Promise<unknown> {
  return api("PUT", `/api/users/${encodeURIComponent(id)}/global-settings`, settings);
}
export function getGlobalContributions(id: string): Promise<any> {
  return api("GET", `/api/plugin-contributions/user-global/${encodeURIComponent(id)}`);
}
export function saveGlobalContribution(
  userId: string,
  pluginName: string,
  contributionId: string,
  values: Record<string, unknown>,
): Promise<unknown> {
  return api(
    "PUT",
    `/api/plugin-contributions/user-global/${encodeURIComponent(userId)}/${encodeURIComponent(pluginName)}/${encodeURIComponent(contributionId)}`,
    { values },
  );
}

export function addBinding(userId: string, scriptInstanceId: string): Promise<unknown> {
  return api("POST", `/api/users/${encodeURIComponent(userId)}/bindings`, {
    scriptInstanceId,
    enabled: true,
    notifyEnabled: true,
    preRunScript: "",
    preRunOnceOnly: false,
    postRunScript: "",
    postRunOnFinalOnly: false,
    smtpTo: "",
    runDays: -1,
    maxSuccessfulRunsPerDay: -1,
  });
}
export function removeBinding(userId: string, scriptInstanceId: string): Promise<unknown> {
  return api(
    "DELETE",
    `/api/users/${encodeURIComponent(userId)}/bindings/${encodeURIComponent(scriptInstanceId)}`,
  );
}
export function saveBinding(userId: string, binding: Record<string, unknown>): Promise<unknown> {
  const scriptInstanceId = String(binding.scriptInstanceId || "");
  return api(
    "PUT",
    `/api/users/${encodeURIComponent(userId)}/bindings/${encodeURIComponent(scriptInstanceId)}`,
    {
      scriptInstanceId,
      enabled: binding.enabled !== false,
      notifyEnabled: binding.notifyEnabled !== false,
      smtpTo: binding.smtpTo || "",
      preRunScript: binding.preRunScript || "",
      preRunOnceOnly: binding.preRunOnceOnly === true,
      postRunScript: binding.postRunScript || "",
      postRunOnFinalOnly: binding.postRunOnFinalOnly === true,
      runDays: typeof binding.runDays === "number" ? binding.runDays : -1,
      maxSuccessfulRunsPerDay:
        typeof binding.maxSuccessfulRunsPerDay === "number"
          ? binding.maxSuccessfulRunsPerDay
          : -1,
      configInputs: binding.configInputs || {},
    },
  );
}
export function reorderBindings(userId: string, ids: string[]): Promise<unknown> {
  return api("PUT", `/api/users/${encodeURIComponent(userId)}/bindings/order`, { ids });
}

export function uploadAvatar(userId: string, mimeType: string, data: string): Promise<unknown> {
  return api("POST", `/api/users/${encodeURIComponent(userId)}/avatar`, { mimeType, data });
}
export function removeAvatar(userId: string): Promise<unknown> {
  return api("DELETE", `/api/users/${encodeURIComponent(userId)}/avatar`);
}

export function browseNativeDialog(payload: {
  kind: "file" | "folder";
  title: string;
  initialPath?: string;
  filter?: string;
}): Promise<unknown> {
  return api("POST", "/api/native-dialog", payload);
}

export function getEditConfigStatus(userId: string, scriptInstanceId: string): Promise<any> {
  return api(
    "GET",
    `/api/users/${encodeURIComponent(userId)}/bindings/${encodeURIComponent(scriptInstanceId)}/edit-config`,
  );
}
export function editConfig(
  userId: string,
  scriptInstanceId: string,
  payload: Record<string, unknown>,
): Promise<unknown> {
  return api(
    "POST",
    `/api/users/${encodeURIComponent(userId)}/bindings/${encodeURIComponent(scriptInstanceId)}/edit-config`,
    payload,
  );
}
export function listEditSessions(): Promise<any> {
  return api("GET", "/api/scripts/edit-sessions");
}
