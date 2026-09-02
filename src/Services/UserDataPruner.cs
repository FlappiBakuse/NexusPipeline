using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services.Execution;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

/// <summary>
/// 历史用户名目录维护工具：显式扫描 data/{脚本Id}/ 下未对应任何当前绑定 UserId 的遗留目录，
/// 仅在「无活动运行、无编辑会话、不在 swap 锁内」时允许删除。默认不自动执行，只提供确认式入口（CLI/API）。
/// 惰性遗留语义不变：恢复白名单与运行读写继续跳过这类目录，本工具是唯一的显式清理途径。
/// </summary>
internal sealed class UserDataPruner
{
    /// <summary>脚本级保留名（非用户目录，无论是否绑定都不视作遗留候选）。
    /// work 为 v0.13.2 起的会话事务工作区；script/swap-backup 为 v0.13.0 及更早的旧布局名，防御性保留。</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase) { "work", "script", "swap-backup" };

    private readonly Func<IReadOnlyList<NexusUser>> _snapshotUsers;

    private readonly ExecutionStateStore _state;

    public UserDataPruner(Func<IReadOnlyList<NexusUser>> snapshotUsers, ExecutionStateStore state)
    {
        _snapshotUsers = snapshotUsers;
        _state = state;
    }

    /// <summary>
    /// 归属判定（纯函数）：返回给定目录名中不属于任何当前绑定 UserId 的遗留键（排除保留名）。
    /// 判定只认稳定 UserId——历史用户名目录与任何已绑定 Id 都不匹配时即遗留。
    /// </summary>
    public static IReadOnlyList<string> ClassifyLegacyUserKeys(
        IReadOnlyList<NexusUser> users,
        string scriptId,
        IEnumerable<string> directoryNames)
    {
        var bound = new HashSet<string>(StringComparer.Ordinal);
        foreach (NexusUser user in users)
        {
            if (string.IsNullOrWhiteSpace(user.Id))
            {
                continue;
            }
            bool boundToScript = user.Bindings.Any(binding =>
                string.Equals(binding.ScriptInstanceId, scriptId, StringComparison.Ordinal));
            if (boundToScript)
            {
                bound.Add(user.Id);
            }
        }
        return directoryNames
            .Where(name => !ReservedNames.Contains(name) && !bound.Contains(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>扫描全部脚本的遗留用户目录候选（含目录内条目数，便于展示）。</summary>
    public IReadOnlyList<LegacyDataCandidate> FindCandidates()
    {
        var candidates = new List<LegacyDataCandidate>();
        if (!Directory.Exists(AppPaths.DataDir))
        {
            return candidates;
        }
        IReadOnlyList<NexusUser> users = _snapshotUsers();
        foreach (string scriptDir in Directory.GetDirectories(AppPaths.DataDir))
        {
            string scriptId = Path.GetFileName(scriptDir);
            IEnumerable<string> names = Directory.GetDirectories(scriptDir)
                .Select(Path.GetFileName)
                .Where(name => name is not null)
                .Cast<string>();
            foreach (string legacyKey in ClassifyLegacyUserKeys(users, scriptId, names))
            {
                string dir = Path.Combine(scriptDir, legacyKey);
                candidates.Add(new LegacyDataCandidate(scriptId, legacyKey, dir, CountEntries(dir)));
            }
        }
        return candidates;
    }

    /// <summary>
    /// 删除一个遗留用户目录（带活动守卫）。成功时 Ok=true；失败时 Error 携带中文原因、Code 携带结构化结果码
    /// （bound=已绑定 / running=活动运行 / editing=编辑会话 / locked=交换锁 / missing=不存在 /
    /// invalid=路径非法 / failed=删除失败），供 API 映射 HTTP 状态。
    /// </summary>
    public PruneResult Prune(string scriptId, string userKey, string auditSource)
    {
        if (string.IsNullOrWhiteSpace(scriptId) || string.IsNullOrWhiteSpace(userKey))
        {
            return PruneResult.Fail("invalid", "脚本 ID 与用户键不能为空");
        }
        string scriptDir = Path.Combine(AppPaths.DataDir, scriptId);
        string target;
        try
        {
            target = Path.GetFullPath(Path.Combine(scriptDir, userKey));
        }
        catch (Exception ex)
        {
            return PruneResult.Fail("invalid", $"目标路径非法：{ex.Message}");
        }
        string dataRoot = Path.GetFullPath(AppPaths.DataDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string scriptRoot = Path.GetFullPath(scriptDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!scriptRoot.StartsWith(dataRoot, StringComparison.OrdinalIgnoreCase)
            || !target.StartsWith(scriptRoot, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(target), userKey, StringComparison.Ordinal))
        {
            return PruneResult.Fail("invalid", "目标路径不在数据目录内，已拒绝");
        }
        if (!Directory.Exists(target))
        {
            return PruneResult.Fail("missing", "目标目录不存在");
        }
        // 归属复核：仍是遗留目录才允许删除（防止并发「新增绑定/改名」后误删当前用户数据）。
        if (!ClassifyLegacyUserKeys(_snapshotUsers(), scriptId, new[] { userKey }).Contains(userKey, StringComparer.Ordinal))
        {
            return PruneResult.Fail("bound", "该目录已对应当前用户绑定，拒绝清理");
        }
        // 守卫 1：无活动运行（脚本级 TargetId 或任一活动准入资源引用该脚本）。
        foreach (RunningExecution exec in _state.Active)
        {
            if (string.Equals(exec.TargetId, scriptId, StringComparison.Ordinal))
            {
                return PruneResult.Fail("running", $"脚本「{exec.TargetName}」正在运行，拒绝清理");
            }
        }
        foreach (ExecutionAdmissionEntry entry in _state.ActiveAdmissions)
        {
            if (entry.Profile.Resources.ScriptIds.Contains(scriptId))
            {
                return PruneResult.Fail("running", $"脚本存在活动运行（{entry.TargetName}），拒绝清理");
            }
        }
        // 守卫 2：无编辑会话。
        if (UserConfigManager.EditSessions.ContainsKey(scriptId))
        {
            return PruneResult.Fail("editing", "脚本存在配置编辑会话，拒绝清理");
        }
        // 守卫 3：不在 swap 锁内（跨进程互斥探测）。
        if (!ConfigSwapPrimitives.TryProbeSwapLock(scriptId))
        {
            return PruneResult.Fail("locked", "脚本配置交换操作进行中，请稍后重试");
        }
        try
        {
            Directory.Delete(target, recursive: true);
            Audit.Log(auditSource, "清理历史用户名目录", $"{scriptId}/{userKey}（{target}）");
            Logger.Info($"[维护] 已清理历史用户名目录：{target}");
            return PruneResult.Success();
        }
        catch (Exception ex)
        {
            Logger.Warn($"[维护] 清理历史用户名目录失败（{target}）：{ex.Message}");
            return PruneResult.Fail("failed", $"删除失败：{ex.Message}");
        }
    }

    private static long CountEntries(string dir)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories).LongCount();
        }
        catch (Exception)
        {
            return 0;
        }
    }
}

/// <summary>历史用户名目录候选（惰性遗留，未绑定任何当前 UserId）。</summary>
internal sealed record LegacyDataCandidate(string ScriptId, string UserKey, string Dir, long ItemCount);

/// <summary>清理结果：Succeeded=true 表示已删除；失败时 Code 为结构化结果码（见 <see cref="UserDataPruner.Prune"/>）。</summary>
internal sealed record PruneResult(bool Succeeded, string? Code, string? Error)
{
    public static PruneResult Success() => new(true, null, null);

    public static PruneResult Fail(string code, string error) => new(false, code, error);
}
