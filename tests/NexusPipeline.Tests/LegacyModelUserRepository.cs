using NexusPipeline.App.Abstractions;
using NexusPipeline.Models;

namespace NexusPipeline.Tests;

/// <summary>
/// 用户仓储测试替身基类：替身以迁移前的脚本嵌套用户模型（ScriptUser）表达语义，
/// 这里把该模型适配到 Resolve 契约。生产代码已统一走全局用户快照
/// （见 RuntimeUserRepository），嵌套模型仅存在于测试替身。
/// </summary>
internal abstract class LegacyModelUserRepository : IUserRepository
{
    public abstract ScriptUser? FindEnabled(ScriptInstance script, string? userName);

    public abstract IReadOnlyList<string> EnabledNames(ScriptInstance script);

    public ResolvedScriptUser? ResolveEnabledBinding(
        ScriptInstance script,
        string? userName,
        IReadOnlyList<NexusUser>? users = null)
    {
        ScriptUser? legacy = FindEnabled(script, userName);
        return legacy is null ? null : FromLegacy(script.Id, legacy);
    }

    public IReadOnlyList<ResolvedScriptUser> ResolveEnabledBindings(
        ScriptInstance script,
        IReadOnlyList<NexusUser>? users = null)
    {
        return EnabledNames(script)
            .Select(name => ResolveEnabledBinding(script, name))
            .Where(item => item is not null)
            .Cast<ResolvedScriptUser>()
            .ToList();
    }

    private static ResolvedScriptUser FromLegacy(string scriptId, ScriptUser legacy)
    {
        return new ResolvedScriptUser(
            "",
            legacy.Name,
            new UserScriptBinding
            {
                ScriptInstanceId = scriptId,
                Enabled = legacy.Enabled,
                PreRunScript = legacy.PreRunScript,
                PreRunOnceOnly = legacy.PreRunOnceOnly,
                PostRunScript = legacy.PostRunScript,
                PostRunOnFinalOnly = legacy.PostRunOnFinalOnly,
            });
    }
}
