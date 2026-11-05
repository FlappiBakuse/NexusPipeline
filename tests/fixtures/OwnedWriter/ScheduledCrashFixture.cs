using System.Text.Json;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility;
using NexusPipeline.Extensibility.Lifecycle;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Extensibility.Scheduling;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Platform.Storage.Sqlite;

internal static class ScheduledCrashFixture
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is not [var root, var report, var effect]) return 2;
        OwnedFixtureEvidence.RequirePath(root); OwnedFixtureEvidence.RequirePath(report); OwnedFixtureEvidence.RequirePath(effect);
        try
        {
            var instance = new InstanceRoot(new()).OpenOrCreate(Path.Combine(root, "runtime"));
            var database = StateDatabase.CreateFresh(instance);
            using var lease = InstanceLease.Acquire(instance, new());
            var roots = new PayloadApplyRoots(Path.Combine(root, "host"), Path.Combine(instance.RuntimeRoot, "extensions"));
            var index = new StartupJournalRecovery(instance, new(), database, roots).Recover(lease);
            var lifecycle = new HostLifecycle(index, TimeProvider.System);
            var resources = new ResourceLeaseManager(lifecycle);
            var execution = new ExecutionAdmissionService(instance, database, new(), lease, resources, index, new(), new(5, 6, 7));
            var clock = new FixedClock(DateTimeOffset.UtcNow);
            var scheduling = new PersistentScheduleService(instance, database, lease, resources, lifecycle, index, execution, clock);
            var manifest = new ExtensionManifest { Format = "nexus.extension.manifest", Version = 1, ExtensionId = "scheduled-crash-fixture",
                ArtifactName = "ScheduledCrashFixture", DisplayName = "Scheduled crash fixture", PackageVersion = "0.5.0",
                Kind = "managed-code", MinHostVersion = "0.18.0", SdkVersion = "0.1.0", ContractFingerprint = SdkIdentity.ContractFingerprint,
                Capabilities = ["schedule.register"], Permissions = new(),
                Managed = new() { EntryAssembly = "ScheduledCrashFixture.dll", EntryType = "Fixture.Extension" } };
            var handler = new Handler(instance, database, lease, resources, clock, report, effect);
            await using var scope = await new ExtensionActivator(new()).ActivateManagedAsync(JsonSerializer.SerializeToUtf8Bytes(manifest),
                manifest.ExtensionId, (admitted, actualScope) =>
                    new Extension(token => scheduling.CreateSchedulePort(admitted, actualScope).RegisterAsync("daily", handler, token)), default);
            scheduling.Start();
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 1;
        }
        catch (Exception error)
        {
            if (!File.Exists(report)) OwnedFixtureEvidence.WriteReport(report, new { status = "refused", errorType = error.GetType().FullName });
            return 1;
        }
    }

    private sealed class Handler(RuntimeInstance instance, StateDatabase database, InstanceLease lease, ResourceLeaseManager resources,
        TimeProvider clock, string report, string effect) : IScheduledWorkPlanningHandler
    {
        private readonly Guid _task = Guid.NewGuid();
        public ValueTask<ScheduledWorkPlans> ReadPlansAsync(CancellationToken cancellationToken)
        {
            var now = clock.GetUtcNow(); cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new ScheduledWorkPlans { Format = "nexus.schedule.work-plans", Version = 1, Revision = 3,
                Tasks = [new() { TaskId = _task.ToString("D"), Revision = 2, IsEnabled = true, TimeZoneId = "UTC",
                    Weekdays = [(long)now.DayOfWeek], LocalMinute = now.Hour * 60 + now.Minute,
                    TimeoutSeconds = 120, NotificationTaskIds = [] }] });
        }
        public ValueTask<ScheduledWorkResult> ExecuteAsync(ScheduledInvocation invocation, CancellationToken cancellationToken) =>
            throw new IOException("unversioned_crash_callback_forbidden");
        public async ValueTask<ScheduledWorkResult> ExecutePlannedAsync(ScheduledPlannedInvocation context, CancellationToken cancellationToken)
        {
            if (context.PlansRevision != 3 || context.TaskRevision != 2) throw new IOException("scheduled_crash_revision_unbound");
            if (new ScheduledProviderPublication(instance, database, new(), lease, resources, clock).ReconcilePriorOwners())
                throw new IOException("live_scheduled_owner_was_closed");
            File.WriteAllText(effect, context.Invocation.OccurrenceId);
            OwnedFixtureEvidence.WriteReport(report, new { status = "callbackRunning", instance,
                occurrenceId = context.Invocation.OccurrenceId, processId = Environment.ProcessId, liveReconcileRejected = true });
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new IOException("scheduled_crash_callback_unexpected_return");
        }
    }
    private sealed class Extension(Func<CancellationToken, ValueTask> activate) : IExtension
    {
        public ValueTask ActivateAsync(IExtensionScope scope, CancellationToken cancellationToken) => activate(cancellationToken);
    }
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
