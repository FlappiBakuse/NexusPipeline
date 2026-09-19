using NexusPipeline.Modules.Scripts.Queries;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Users.Bindings;
using NexusPipeline.Modules.Users.Contracts;
using NexusPipeline.Modules.Users;

namespace NexusPipeline.Modules.Users.Queries;

internal sealed record UserBindingReadModel(
    string ScriptInstanceId,
    string ScriptName,
    bool Enabled,
    IReadOnlyDictionary<string, string> ConfigInputs,
    string PreRunScript,
    bool PreRunOnceOnly,
    string PostRunScript,
    bool PostRunOnFinalOnly,
    bool NotifyEnabled,
    string SmtpTo,
    int RunDays,
    int MaxSuccessfulRunsPerDay,
    UserBindingEffectiveReadModel Effective,
    UserBindingLocksReadModel Locks);

internal sealed record UserBindingEffectiveReadModel(
    bool Enabled,
    string PreRunScript,
    bool PreRunOnceOnly,
    string PostRunScript,
    bool PostRunOnFinalOnly,
    bool NotifyEnabled,
    string SmtpTo,
    int RunDays,
    int MaxSuccessfulRunsPerDay,
    bool Participates);

internal sealed record UserBindingLocksReadModel(
    bool General,
    bool Notification,
    bool Advanced);

internal sealed record UserReadModel(
    string Id,
    int Index,
    string Name,
    string Remark,
    int BindingCount,
    DateTime? NextRunAt,
    string? NextQueueName,
    IReadOnlyList<UserBindingReadModel> Bindings);

/// <summary>用户读取用例：组合脚本、队列和全局覆盖后的稳定读取模型。</summary>
internal sealed class UserQueries
{
    private readonly IUserSnapshotReader _users;
    private readonly ScriptQueries _scripts;
    private readonly IUserScheduleProjection _schedule;

    public UserQueries(
        IUserSnapshotReader users,
        ScriptQueries scripts,
        IUserScheduleProjection schedule)
    {
        _users = users;
        _scripts = scripts;
        _schedule = schedule;
    }

    public IReadOnlyList<UserReadModel> List()
    {
        List<ScriptInstance> scripts = _scripts.ListEffective().ToList();
        return _users.Snapshot()
            .OrderBy(user => user.Index)
            .Select(user => Build(user, scripts))
            .ToList();
    }

    public IReadOnlyList<NexusUser> ListEntities()
    {
        return _users.Snapshot()
            .OrderBy(user => user.Index)
            .ToList();
    }

    public UserReadModel? Find(string id)
    {
        NexusUser? user = _users.FindById(id);
        if (user is null)
        {
            return null;
        }
        return Build(user, _scripts.ListEffective());
    }

    public IReadOnlyList<UserBindingReadModel>? ListBindings(string userId)
    {
        return Find(userId)?.Bindings;
    }

    public UserBindingOverrides? FindGlobalSettings(string userId)
    {
        return _users.FindById(userId)?.BindingOverrides?.Clone();
    }

    private UserReadModel Build(
        NexusUser user,
        IReadOnlyList<ScriptInstance> scripts)
    {
        (string QueueName, DateTime TriggerTime)? next = _schedule.NextTriggerForUser(user);
        List<UserBindingReadModel> bindings = user.Bindings
            .Select(binding => BuildBinding(user, binding, scripts))
            .ToList();
        return new UserReadModel(
            user.Id,
            user.Index,
            user.Name,
            user.Remark,
            bindings.Count,
            next?.TriggerTime,
            next?.QueueName,
            bindings);
    }

    private static UserBindingReadModel BuildBinding(
        NexusUser user,
        UserScriptBinding binding,
        IReadOnlyList<ScriptInstance> scripts)
    {
        ScriptInstance? script = scripts.FirstOrDefault(item => item.Id == binding.ScriptInstanceId);
        UserScriptBinding effective = UserBindingOverrideResolver.Resolve(user, binding);
        (bool General, bool Notification, bool Advanced) locks = UserBindingOverrideResolver.Locks(user);
        return new UserBindingReadModel(
            binding.ScriptInstanceId,
            script?.Name ?? "（脚本实例不存在）",
            binding.Enabled,
            binding.ConfigInputs,
            binding.PreRunScript,
            binding.PreRunOnceOnly,
            binding.PostRunScript,
            binding.PostRunOnFinalOnly,
            binding.NotifyEnabled,
            binding.SmtpTo,
            binding.RunDays,
            binding.MaxSuccessfulRunsPerDay,
            new UserBindingEffectiveReadModel(
                effective.Enabled,
                effective.PreRunScript,
                effective.PreRunOnceOnly,
                effective.PostRunScript,
                effective.PostRunOnFinalOnly,
                effective.NotifyEnabled,
                effective.SmtpTo,
                effective.RunDays,
                effective.MaxSuccessfulRunsPerDay,
                effective.Participates),
            new UserBindingLocksReadModel(locks.General, locks.Notification, locks.Advanced));
    }
}
