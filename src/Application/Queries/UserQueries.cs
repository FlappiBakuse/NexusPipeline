using NexusPipeline.App.State;
using NexusPipeline.Models;
using NexusPipeline.Services;

namespace NexusPipeline.App.Queries;

internal sealed record UserBindingReadModel(
    string ScriptInstanceId,
    string ScriptName,
    bool Enabled,
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
    private readonly RuntimeEntityState _state;
    private readonly ScriptQueries _scripts;
    private readonly Scheduler _scheduler;

    public UserQueries(RuntimeEntityState state, ScriptQueries scripts, Scheduler scheduler)
    {
        _state = state;
        _scripts = scripts;
        _scheduler = scheduler;
    }

    public IReadOnlyList<UserReadModel> List()
    {
        List<ScriptInstance> scripts = _scripts.ListEffective().ToList();
        List<DispatchQueue> queues = _state.SnapshotQueues();
        return _state.SnapshotUsers()
            .OrderBy(user => user.Index)
            .Select(user => Build(user, scripts, queues))
            .ToList();
    }

    public IReadOnlyList<NexusUser> ListEntities()
    {
        return _state.SnapshotUsers()
            .OrderBy(user => user.Index)
            .ToList();
    }

    public UserReadModel? Find(string id)
    {
        NexusUser? user = _state.FindUser(id);
        if (user is null)
        {
            return null;
        }
        return Build(user, _scripts.ListEffective(), _state.SnapshotQueues());
    }

    public IReadOnlyList<UserBindingReadModel>? ListBindings(string userId)
    {
        return Find(userId)?.Bindings;
    }

    public UserBindingOverrides? FindGlobalSettings(string userId)
    {
        return _state.FindUser(userId)?.BindingOverrides?.Clone();
    }

    private UserReadModel Build(
        NexusUser user,
        IReadOnlyList<ScriptInstance> scripts,
        IReadOnlyList<DispatchQueue> queues)
    {
        (string QueueName, DateTime TriggerTime)? next = _scheduler.NextTriggerForUser(user, queues);
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
