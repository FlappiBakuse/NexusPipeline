using NexusPipeline.Extensibility;
using NexusPipeline.Models;
using NexusPipeline.Services;
using NexusPipeline.Services.Execution;

namespace NexusPipeline.App.Abstractions;

/// <summary>脚本配置读取端口。执行域只依赖该端口，不直接访问组合根中的共享列表。</summary>
internal interface IScriptRepository
{
    ScriptInstance? FindById(string id);

    IReadOnlyList<ScriptInstance> Snapshot();
}

/// <summary>调度队列读取端口。写入仍由 Web/CLI 的现有事务路径负责。</summary>
internal interface IQueueRepository
{
    DispatchQueue? FindById(string id);

    IReadOnlyList<DispatchQueue> Snapshot();
}

/// <summary>执行计划所需的单时刻仓储快照，队列与其脚本引用在同一数据锁内复制。</summary>
internal interface IExecutionSnapshotProvider
{
    ExecutionScriptSnapshot? SnapshotScript(string scriptId);

    ExecutionQueueSnapshot? SnapshotQueue(string queueId);
}

internal sealed record ExecutionScriptSnapshot(
    ScriptInstance Script,
    IReadOnlyList<NexusUser>? Users = null);

internal sealed record ExecutionQueueSnapshot(
    DispatchQueue Queue,
    IReadOnlyList<ScriptInstance> Scripts,
    IReadOnlyList<NexusUser>? Users = null);

/// <summary>一次执行计划中冻结的用户身份和绑定设置。</summary>
internal sealed record ResolvedScriptUser(
    string UserId,
    string UserName,
    UserScriptBinding Binding)
{
    public string UserKey => string.IsNullOrWhiteSpace(UserId) ? UserName : UserId;

    public ScriptUser ToLegacyScriptUser()
    {
        return new ScriptUser
        {
            Name = UserName,
            Enabled = Binding.Enabled,
            PreRunScript = Binding.PreRunScript,
            PreRunOnceOnly = Binding.PreRunOnceOnly,
            PostRunScript = Binding.PostRunScript,
            PostRunOnFinalOnly = Binding.PostRunOnFinalOnly,
        };
    }
}

/// <summary>脚本用户读取端口，集中处理并发快照与启用用户规则。</summary>
internal interface IUserRepository
{
    ScriptUser? FindEnabled(ScriptInstance script, string? userName);

    IReadOnlyList<string> EnabledNames(ScriptInstance script);

    /// <summary>
    /// 按全局用户快照解析一个启用绑定。旧测试/兼容仓储未实现时回退到 ScriptUser，保证旧执行契约可逐步迁移。
    /// </summary>
    ResolvedScriptUser? ResolveEnabledBinding(
        ScriptInstance script,
        string? userName,
        IReadOnlyList<NexusUser>? users = null)
    {
        if (users is not null)
        {
            if (string.IsNullOrWhiteSpace(userName))
            {
                return null;
            }
            foreach (NexusUser user in users.OrderBy(item => item.Index))
            {
                if (!string.Equals(user.Name, userName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                UserScriptBinding? binding = user.Bindings.FirstOrDefault(item =>
                    item.Participates && string.Equals(item.ScriptInstanceId, script.Id, StringComparison.Ordinal));
                return binding is null ? null : new ResolvedScriptUser(user.Id, user.Name, binding.Clone());
            }
            return null;
        }

        ScriptUser? legacy = FindEnabled(script, userName);
        return legacy is null
            ? null
            : new ResolvedScriptUser(
                "",
                legacy.Name,
                new UserScriptBinding
                {
                    ScriptInstanceId = script.Id,
                    Enabled = legacy.Enabled,
                    PreRunScript = legacy.PreRunScript,
                    PreRunOnceOnly = legacy.PreRunOnceOnly,
                    PostRunScript = legacy.PostRunScript,
                    PostRunOnFinalOnly = legacy.PostRunOnFinalOnly,
                });
    }

    /// <summary>按全局 Index 返回脚本已绑定且启用的用户；users 为空时兼容旧嵌套模型。</summary>
    IReadOnlyList<ResolvedScriptUser> ResolveEnabledBindings(
        ScriptInstance script,
        IReadOnlyList<NexusUser>? users = null)
    {
        if (users is not null)
        {
            return users
                .OrderBy(item => item.Index)
                .Select(user => new
                {
                    User = user,
                    Binding = user.Bindings.FirstOrDefault(item =>
                        item.Participates && string.Equals(item.ScriptInstanceId, script.Id, StringComparison.Ordinal)),
                })
                .Where(item => item.Binding is not null)
                .Select(item => new ResolvedScriptUser(item.User.Id, item.User.Name, item.Binding!.Clone()))
                .ToList();
        }

        return EnabledNames(script)
            .Select(name => ResolveEnabledBinding(script, name))
            .Where(item => item is not null)
            .Cast<ResolvedScriptUser>()
            .ToList();
    }
}

/// <summary>设置读取端口，避免业务服务为读取设置而反向依赖 RuntimeContext。</summary>
internal interface ISettingsProvider
{
    AppSettings Current { get; }
}

/// <summary>运行天数每日递减端口：调度器每日首次 tick 调用一次；返回是否有绑定发生递减。</summary>
internal interface IUserRunDaysWriter
{
    /// <summary>对所有 RunDays &gt; 0 的绑定递减 1（降至 0 后不再参与运行）；返回是否发生变化。</summary>
    bool DecrementDaily();
}

/// <summary>历史持久化端口，执行域不依赖历史文件实现。</summary>
internal interface IHistoryStore
{
    HistorySaveResult Save(RunRecord record, List<string> attemptLogs);

    void Cleanup(int retentionDays);
}

/// <summary>历史提交结果：调用方只发布已经确定的不可变快照，并显式感知持久化告警。</summary>
internal sealed record HistorySaveResult(RunRecord Record, string? PersistenceWarning);

/// <summary>执行应用端口，供 Web、CLI、Scheduler 共享执行入口。</summary>
internal interface IExecutionService
{
    RunningExecution StartScript(string scriptId, string mode, string source, string? userName = null);

    RunningExecution StartQueue(string queueId, string mode, string source);

    void Cancel(string runId, string source);
}

/// <summary>调度器使用的冻结队列计划入口；普通 Web/CLI 入口仍按 ID 构建即时计划。</summary>
internal interface IFrozenQueueExecutionService
{
    RunningExecution StartQueue(QueueExecutionPlan plan, string mode, string source);
}

/// <summary>通知应用端口，执行域不感知具体 NotificationDispatcher 实现。</summary>
internal interface INotificationService
{
    Task NotifyScriptAsync(ScriptInstance script, RunRecord record);

    Task NotifyScriptAsync(ScriptInstance script, RunRecord record, UserScriptBinding binding)
    {
        return NotifyScriptAsync(script, record);
    }

    Task NotifyQueueAsync(DispatchQueue queue, List<RunRecord> records);
}

/// <summary>插件能力解析端口，校验与配置编辑流程只依赖能力，不依赖 PluginManager。</summary>
internal interface IPluginCapabilityResolver
{
    bool SupportsEmulator(string pluginName);

    ScriptProfile? ResolveProfile(string pluginName, string rootPath);
}

/// <summary>
/// 配置交换恢复的只读数据源端口：恢复路径按脚本/用户快照工作，
/// 不再反向依赖组合根 RuntimeContext。具体适配由组合根在启动时注入（RuntimeInitializer 装配）。
/// </summary>
internal interface IConfigRecoveryDataSource
{
    ScriptInstance? FindScript(string scriptId);

    IReadOnlyList<NexusUser> SnapshotUsers();
}
