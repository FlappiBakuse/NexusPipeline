/** 配置编辑事务的会话恢复匹配。宿主在刷新或服务重启后仍持有锁定编辑事务，
 *  前端据此把用户带回进行中的 ConfigEditFlow，避免事务悬空。 */

export interface EditSessionUserLike {
  id?: string;
  name?: string;
  bindings?: Array<{ scriptInstanceId?: string }> | null;
}

export interface EditSessionScriptLike {
  id?: string;
  name?: string;
}

export interface RestorableEditSession {
  userId: string;
  scriptId: string;
  userName: string;
  scriptName: string;
  mode: string;
}

function matchUser(
  session: { userId?: unknown; userName?: unknown },
  users: readonly EditSessionUserLike[],
): EditSessionUserLike | undefined {
  const userId = String(session.userId ?? "").trim();
  const userName = String(session.userName ?? "").trim();
  return users.find((candidate) => {
    if (userId && String(candidate.id ?? "") === userId) return true;
    return Boolean(userName) && String(candidate.name ?? "") === userName;
  });
}

/**
 * 从 `GET /api/scripts/edit-sessions` 的响应中选出第一个仍可恢复的会话。
 * 匹配语义保持 v0.15.4：用户可由 userId 或 userName 命中，会话 scriptId 必须命中该用户绑定，
 * 且脚本实例仍然存在；payload 结构异常时按无会话处理。
 */
export function findRestorableEditSession(
  sessions: unknown,
  users: readonly EditSessionUserLike[] | null | undefined,
  scripts: readonly EditSessionScriptLike[] | null | undefined,
): RestorableEditSession | null {
  if (!Array.isArray(sessions) || !Array.isArray(users) || !Array.isArray(scripts)) return null;
  for (const raw of sessions) {
    if (!raw || typeof raw !== "object") continue;
    const session = raw as { userId?: unknown; userName?: unknown; scriptId?: unknown; editMode?: unknown };
    const scriptId = String(session.scriptId ?? "").trim();
    if (!scriptId) continue;
    const user = matchUser(session, users);
    if (!user) continue;
    const binding = (user.bindings || []).find(
      (item) => String(item?.scriptInstanceId ?? "") === scriptId,
    );
    if (!binding) continue;
    const script = scripts.find((item) => String(item.id ?? "") === scriptId);
    if (!script) continue;
    const mode = String(session.editMode ?? "").trim() || "normal";
    return {
      userId: String(user.id ?? ""),
      scriptId,
      userName: String(user.name ?? ""),
      scriptName: String(script.name ?? ""),
      mode,
    };
  }
  return null;
}
