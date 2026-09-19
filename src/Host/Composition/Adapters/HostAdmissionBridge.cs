using NexusPipeline.Modules.Configuration.Contracts;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.Execution.Contracts;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Queues.Contracts;
using NexusPipeline.Modules.Scripts.Contracts;
using NexusPipeline.Modules.Settings.Contracts;
using NexusPipeline.Modules.Settings;

namespace NexusPipeline.Host.Composition.Adapters;

/// <summary>
/// 有限的 Host 准入绑定。它只持有一个已经构造好的执行门面，不保存容器、解析器或服务字典。
/// </summary>
internal sealed class HostAdmissionBridge :
    ISettingsMutationGate,
    IPluginConfigurationMutationGate,
    IScriptMutationAdmission,
    IQueueMutationAdmission
{
    private ExecutionDispatcher? _dispatcher;
    private readonly SettingsState _settings;

    internal HostAdmissionBridge(SettingsState settings)
    {
        _settings = settings;
    }

    internal void BindOnce(ExecutionDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        if (Interlocked.CompareExchange(ref _dispatcher, dispatcher, null) is not null)
        {
            throw new InvalidOperationException("HostAdmissionBridge 只能绑定一次执行门面。");
        }
    }

    public bool TryExecute(Action mutation, out string? failureCode)
    {
        ExecutionDispatcher? dispatcher = Volatile.Read(ref _dispatcher);
        if (dispatcher is null)
        {
            failureCode = "host_unavailable";
            return false;
        }
        return dispatcher.TryExecuteHostConfigurationMutation(
            () =>
            {
                lock (_settings.MutationLock)
                {
                    mutation();
                }
            },
            out failureCode);
    }

    public ScriptMutationAdmissionResult TryExecute(
        string scriptId,
        string? userName,
        Action mutation)
    {
        ExecutionDispatcher? dispatcher = Volatile.Read(ref _dispatcher);
        if (dispatcher is null)
        {
            return new ScriptMutationAdmissionResult(false, Array.Empty<string>(), "host_unavailable");
        }

        bool allowed = dispatcher.TryExecuteLeaseMutation(
            scriptId,
            userName,
            mutation,
            out IReadOnlyList<ExecutionLeaseReference> leases,
            out string? failureCode);
        return new ScriptMutationAdmissionResult(
            allowed,
            leases.Select(lease => lease.RunId).Distinct(StringComparer.Ordinal).ToArray(),
            failureCode);
    }

    public QueueMutationAdmissionResult TryExecute(string queueId, Action mutation)
    {
        ExecutionDispatcher? dispatcher = Volatile.Read(ref _dispatcher);
        if (dispatcher is null)
        {
            return new QueueMutationAdmissionResult(false, Array.Empty<string>(), "host_unavailable");
        }

        bool allowed = dispatcher.TryExecuteQueueLeaseMutation(
            queueId,
            mutation,
            out IReadOnlyList<ExecutionLeaseReference> leases,
            out string? failureCode);
        return new QueueMutationAdmissionResult(
            allowed,
            leases.Select(lease => lease.RunId).Distinct(StringComparer.Ordinal).ToArray(),
            failureCode);
    }

    public QueueMutationAdmissionResult TryExecuteAny(Action mutation)
    {
        ExecutionDispatcher? dispatcher = Volatile.Read(ref _dispatcher);
        if (dispatcher is null)
        {
            return new QueueMutationAdmissionResult(false, Array.Empty<string>(), "host_unavailable");
        }

        bool allowed = dispatcher.TryExecuteAnyQueueLeaseMutation(
            mutation,
            out IReadOnlyList<ExecutionLeaseReference> leases,
            out string? failureCode);
        return new QueueMutationAdmissionResult(
            allowed,
            leases.Select(lease => lease.RunId).Distinct(StringComparer.Ordinal).ToArray(),
            failureCode);
    }
}
