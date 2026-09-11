import { getLocale, t } from "./i18n";

export interface NexusAuthWindow extends Window {
  __showTokenPrompt?: () => void;
}

let tokenPromptRenderer: (() => void) | null = null;

/**
 * 访问令牌提示的呈现入口。宿主 shell 以 Vue 组件承载弹窗，认证状态机留在平台层：
 * 探测到 401 或运行期间需要重新认证时由本模块请求呈现。
 */
export function registerTokenPromptRenderer(renderer: (() => void) | null): void {
  tokenPromptRenderer = renderer;
}

function readToken(): string {
  try { return localStorage.getItem("nexus-token") || ""; } catch { return ""; }
}

export function clearToken(): void {
  try { localStorage.removeItem("nexus-token"); } catch { /* 存储不可用时保持当前会话。 */ }
}

/** 校验令牌是否可用于当前宿主；成功时保存令牌并返回 true。 */
export async function verifyToken(value: string): Promise<boolean> {
  const token = String(value || "").trim();
  if (!token) return false;
  try {
    localStorage.setItem("nexus-token", token);
  } catch { /* 当前会话继续使用令牌。 */ }
  try {
    const response = await fetch("/api/status", { headers: { Authorization: `Bearer ${token}`, "X-Nexus-Locale": getLocale() } });
    return response.ok;
  } catch {
    return false;
  }
}

export async function ensureAccessToken(): Promise<boolean> {
  const token = readToken();
  try {
    const response = await fetch("/api/status", { headers: { ...(token ? { Authorization: `Bearer ${token}` } : {}), "X-Nexus-Locale": getLocale() } });
    if (response.status === 401 || response.headers.get("X-Nexus-Auth") === "required") {
      tokenPromptRenderer?.();
      return false;
    }
    return response.ok;
  } catch {
    tokenPromptRenderer?.();
    return false;
  }
}

/** 运行期间 401 重新认证入口：宿主 API 层与运行预览捕获到鉴权失败时统一调用。 */
export function installReauthEntry(): void {
  (window as NexusAuthWindow).__showTokenPrompt = () => {
    clearToken();
    tokenPromptRenderer?.();
  };
}

/** 令牌提示文案；文本来自宿主语言资源，调用时按当前语言解析。 */
export const authPromptText = {
  title: () => t("auth.title"),
  copy: () => t("auth.copy"),
  placeholder: () => t("auth.placeholder"),
  enter: () => t("auth.enter"),
  requiredInput: () => t("auth.required_input"),
  invalid: () => t("auth.invalid"),
};

export function reloadPage(): void {
  window.location.reload();
}
