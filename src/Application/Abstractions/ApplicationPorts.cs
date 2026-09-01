using NexusPipeline.Extensibility;
using NexusPipeline.Models;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Services;
using NexusPipeline.Services.Execution;
using NexusPipeline.Services.Notification;

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
    public string UserKey => UserId;
}

/// <summary>脚本用户读取端口，集中处理并发快照与启用用户规则。</summary>
internal interface IUserRepository
{
    /// <summary>按全局用户快照解析一个启用绑定；users 为空时由实现方取当前并发快照。</summary>
    ResolvedScriptUser? ResolveEnabledBinding(
        ScriptInstance script,
        string? userName,
        IReadOnlyList<NexusUser>? users = null);

    /// <summary>按全局 Index 返回脚本已绑定且启用的用户；users 为空时由实现方取当前并发快照。</summary>
    IReadOnlyList<ResolvedScriptUser> ResolveEnabledBindings(
        ScriptInstance script,
        IReadOnlyList<NexusUser>? users = null);
}

/// <summary>设置读取端口，避免业务服务为读取设置而反向依赖 RuntimeContext。</summary>
internal interface ISettingsProvider
{
    AppSettings Current { get; }
}

/// <summary>历史持久化端口，执行域不依赖历史文件实现。</summary>
internal interface IHistoryStore
{
    HistorySaveResult Save(
        RunRecord record,
        List<string> attemptLogs,
        IReadOnlyList<RunScreenshot> screenshots);

    IReadOnlyDictionary<string, int> GetSuccessfulRunsByUser(DateTime date, string scriptInstanceId);

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

    Task NotifyScriptAsync(
        ScriptInstance script,
        RunRecord record,
        UserScriptBinding binding,
        NotificationImage? image)
    {
        return NotifyScriptAsync(script, record, binding);
    }

    Task NotifyQueueAsync(DispatchQueue queue, List<RunRecord> records);
}

/// <summary>插件能力解析端口，校验与配置编辑流程只依赖能力，不依赖 PluginManager。</summary>
internal interface IPluginCapabilityResolver
{
    bool SupportsEmulator(string pluginName);

    ScriptProfile? ResolveProfile(string pluginName, string rootPath);
}

/// <summary>专项脚本实例的插件可用性端口；运行与配置流程只依赖动态状态，不直接依赖 PluginManager。</summary>
internal interface IPluginAvailability
{
    bool IsKnownPlugin(string pluginName);

    bool IsDataSpecializedPlugin(string pluginName);

    bool IsEnabled(string pluginName);
}

/// <summary>执行域向 managed-code 插件发布用户开始事件的内部端口。</summary>
internal interface IUserRunStartingPublisher
{
    void Publish(PluginUserRunStartingEvent eventData);
}
