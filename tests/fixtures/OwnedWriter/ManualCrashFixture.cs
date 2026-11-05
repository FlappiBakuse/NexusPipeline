using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Scheduling;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Platform.Storage.Sqlite;

internal static class ManualCrashFixture
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is not [var root, var phase, var report] || phase is not ("accepted" or "begun")) return 2;
        OwnedFixtureEvidence.RequirePath(root); OwnedFixtureEvidence.RequirePath(report);
        try
        {
            var instance = new InstanceRoot(new()).OpenOrCreate(Path.Combine(root, "runtime"));
            var database = StateDatabase.CreateFresh(instance);
            using var lease = InstanceLease.Acquire(instance, new());
            var index = new StartupJournalRecovery(instance, new(), database,
                new(Path.Combine(root, "host"), Path.Combine(instance.RuntimeRoot, "extensions"))).Recover(lease);
            var resources = new ResourceLeaseManager(new HostLifecycle(index, TimeProvider.System));
            var registry = new ScheduledWorkRegistry(new Policy());
            var activation = new ExtensionActivation("fixture"); activation.Publish(); var source = new Source();
            using var registration = registry.Register(activation, "tasks", source);
            var observed = (await registry.RequireManualEntry("fixture", "tasks").ReadManualAsync(source.TaskId))!;
            var store = new ManualWorkStore(instance, database, lease, resources, new(), TimeProvider.System);
            var started = DateTimeOffset.UtcNow; var operation = Guid.NewGuid();
            using var work = store.Accept(observed, operation, "fixture:caller", Guid.NewGuid(), started,
                new(TimeProvider.System, TimeProvider.System.GetTimestamp(), TimeSpan.FromMinutes(5), started.AddMinutes(5)), () => { });
            if (phase == "begun") store.Begin(work);
            OwnedFixtureEvidence.WriteReport(report, new { status = "faultReady", instance, operationId = operation,
                taskId = source.TaskId, phase, processId = Environment.ProcessId });
            await Task.Delay(Timeout.InfiniteTimeSpan); return 1;
        }
        catch (Exception error)
        {
            if (!File.Exists(report)) OwnedFixtureEvidence.WriteReport(report, new { status = "refused", code = error.Message, errorType = error.GetType().FullName });
            return 1;
        }
    }

    private sealed class Policy : IScheduledInvocationAuthorization
    {
        public void AssertAllowed(FrozenScheduledInvocation invocation, string extensionId, string providerId) => throw new UnauthorizedAccessException();
    }
    private sealed class Source : IManualWorkSource
    {
        internal Guid TaskId { get; } = Guid.NewGuid();
        public ValueTask<ManualWorkTask?> ReadTaskAsync(Guid taskId, CancellationToken cancellation) =>
            ValueTask.FromResult<ManualWorkTask?>(taskId == TaskId ? new(TaskId, 7, true, "UTC", 300, [], []) : null);
        public ValueTask<ScheduledBusinessResult> ExecuteAsync(FrozenScheduledInvocation invocation, CancellationToken cancellation) => throw new InvalidOperationException();
        public ValueTask<ScheduledBusinessResult> ExecuteManualAsync(ManualInvocation invocation, CancellationToken cancellation) => throw new InvalidOperationException();
    }
}
