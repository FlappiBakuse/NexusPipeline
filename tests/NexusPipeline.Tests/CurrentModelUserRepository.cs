using NexusPipeline.App.Abstractions;
using NexusPipeline.Models;
using NexusPipeline.Services;

namespace NexusPipeline.Tests;

/// <summary>用户仓储测试替身：按当前全局用户与脚本绑定模型解析执行用户。</summary>
internal abstract class CurrentModelUserRepository : IUserRepository
{
    private readonly IReadOnlyList<NexusUser> _users;

    protected CurrentModelUserRepository(params NexusUser[] users)
    {
        _users = users;
    }

    public ResolvedScriptUser? ResolveEnabledBinding(
        ScriptInstance script,
        string? userName,
        IReadOnlyList<NexusUser>? users = null)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            return null;
        }
        return Source(users)
            .OrderBy(user => user.Index)
            .Where(user => string.Equals(user.Name, userName, StringComparison.OrdinalIgnoreCase))
            .Select(user => Resolve(script, user))
            .FirstOrDefault(item => item is not null);
    }

    public IReadOnlyList<ResolvedScriptUser> ResolveEnabledBindings(
        ScriptInstance script,
        IReadOnlyList<NexusUser>? users = null)
    {
        return Source(users)
            .OrderBy(user => user.Index)
            .Select(user => Resolve(script, user))
            .Where(item => item is not null)
            .Cast<ResolvedScriptUser>()
            .ToList();
    }

    private IReadOnlyList<NexusUser> Source(IReadOnlyList<NexusUser>? users)
    {
        return users ?? _users;
    }

    private static ResolvedScriptUser? Resolve(ScriptInstance script, NexusUser user)
    {
        UserScriptBinding? binding = user.Bindings
            .Select(item => UserBindingOverrideResolver.Resolve(user, item))
            .FirstOrDefault(item => item.Participates
                && string.Equals(item.ScriptInstanceId, script.Id, StringComparison.Ordinal));
        return binding is null ? null : new ResolvedScriptUser(user.Id, user.Name, binding);
    }
}
