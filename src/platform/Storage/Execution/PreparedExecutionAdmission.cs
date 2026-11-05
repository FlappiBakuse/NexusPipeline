using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class PreparedExecutionAdmission : IPreparedExecutionAdmission
{
    private readonly object _gate = new();
    private List<IDisposable>? _held;
    private bool _logsReleased;
    internal object Owner { get; }
    internal OperationIdentity Identity { get; }
    internal PreparedExecutionPlan Total { get; }
    internal PreparedAttemptPlan Attempt { get; }
    internal IReadOnlyDictionary<string, PreparedConfigurationRun> Configurations { get; }
    internal IReadOnlyList<IDisposable> LogGuards { get; }
    internal ExecutionTimeBudget TimeBudget { get; }
    internal DateTimeOffset FirstPreparedAtUtc { get; }
    internal PreparedExecutionNetwork? Network { get; }
    internal IReadOnlyDictionary<string, ExecutionProviderRegistry.PreparedExecutionProvider> Providers { get; }
    internal ExecutionRunDisplay? Display { get; init; }
    internal PayloadApplyRoots PayloadRoots { get; }
    internal NexusPipeline.Platform.Storage.Emulation.EmulatorTargetService? Emulators { get; init; }

    internal PreparedExecutionAdmission(object owner, OperationIdentity identity, PreparedExecutionPlan total,
        PreparedAttemptPlan attempt, IReadOnlyDictionary<string, PreparedConfigurationRun> configurations,
        IReadOnlyList<IDisposable> logGuards, List<IDisposable> held, ExecutionTimeBudget timeBudget, PreparedExecutionNetwork? network, PayloadApplyRoots payloadRoots,
        IReadOnlyDictionary<string, ExecutionProviderRegistry.PreparedExecutionProvider>? providers = null, DateTimeOffset? firstPreparedAtUtc = null)
    {
        PayloadRoots = payloadRoots; FirstPreparedAtUtc = firstPreparedAtUtc ?? default;
        Providers = providers ?? new Dictionary<string, ExecutionProviderRegistry.PreparedExecutionProvider>();
        Owner = owner; Identity = identity; Total = total; Attempt = attempt;
        Configurations = configurations; LogGuards = logGuards; _held = held; TimeBudget = timeBudget; Network = network;
    }

    internal void RequireLive() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _held) is null, this);

    internal void ReleaseLogGuards()
    {
        lock (_gate)
        {
            RequireLive();
            if (_logsReleased) return;
            if (LogGuards.Any(guard => !_held!.Contains(guard))) throw new IOException("execution_log_guard_ownership_changed");
            foreach (var guard in LogGuards) _held!.Remove(guard);
            _logsReleased = true;
        }
        for (var index = LogGuards.Count - 1; index >= 0; index--) LogGuards[index].Dispose();
    }

    public void Dispose()
    {
        List<IDisposable>? held;
        lock (_gate) held = Interlocked.Exchange(ref _held, null);
        if (held is null) return;
        for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose();
    }
}
