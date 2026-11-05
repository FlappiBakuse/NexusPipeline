using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Records;
using NexusPipeline.Platform.Storage.Sqlite;

[SupportedOSPlatform("windows")]
internal static class ProviderCrashFixture
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is not [var inputPath, var report]) return 2;
        OwnedFixtureEvidence.RequirePath(inputPath); OwnedFixtureEvidence.RequirePath(report);
        try
        {
            using var input = StrictJson.Parse(File.ReadAllBytes(inputPath)); var value = input.RootElement;
            var runtime = value.GetProperty("runtimeRoot").GetString()!; OwnedFixtureEvidence.RequirePath(runtime);
            var archive = value.GetProperty("archive").GetString()!; OwnedFixtureEvidence.RequirePath(archive);
            var deployed = value.GetProperty("deployed").GetString()!; OwnedFixtureEvidence.RequirePath(deployed);
            var marker = value.GetProperty("marker").GetString()!; OwnedFixtureEvidence.RequirePath(marker);
            var point = value.GetProperty("point").GetString();
            if (point is not ("running" or "collected" or "committed" or "RetentionPrepared" or "HandoffApplied" or "retirementAccepted" or "retirementMaterialized")) throw new IOException("fixture_provider_crash_point_invalid");
            var instance = new InstanceRoot(new()).OpenExisting(runtime);
            using var lease = InstanceLease.Acquire(instance, new());
            var database = StateDatabase.OpenExisting(instance, Path.Combine(Path.GetDirectoryName(inputPath)!, "owner-diagnostics"),
                new JournalStartupAdmission(instance, new()));
            var index = new StartupJournalRecovery(instance, new(), database,
                new(Path.GetDirectoryName(inputPath)!, Path.Combine(runtime, "extensions"))).Recover(lease);
            var resources = new ResourceLeaseManager(new HostLifecycle(index, TimeProvider.System)); var inbox = new ExecutionAdmissionInbox();
            var registry = new ExecutionProviderRegistry(); await using var scope = new ExtensionScope("provider-fixture");
            var package = VerifiedProviderPackage.VerifyControlled(archive, deployed, "provider-fixture", new());
            await new ExecutionRegistrationPort(scope, package, registry).RegisterAsync(default); scope.Publish();
            var service = new ExecutionAdmissionService(instance, database, new(), lease, resources, index, inbox, new(15, 15, 15),
                null, null, null, registry);
            var operation = value.GetProperty("operationId").GetGuid();
            service.Start(operation, "provider-runtime-fixture", new(new BindingTarget(value.GetProperty("bindingId").GetGuid()),
                value.GetProperty("catalogRevision").GetInt64(), value.GetProperty("settingsRevision").GetInt64()), DateTimeOffset.UtcNow);
            var accepted = inbox.Take(operation) ?? throw new IOException("fixture_provider_admission_missing");
            var barrier = point is "RetentionPrepared" or "HandoffApplied" ? new Barrier(instance, database, point, report) : null;
            var session = new AttemptRuntimeSession(instance, database, new(), lease, resources, index, accepted, barrier);
            session.PrepareConfigurationGroups(); var stepId = session.StepIds.Single(); session.PrepareNextStep(stepId);
            session.EnterEffect(stepId);
            var running = session.ExecuteStepAsync(stepId, CancellationToken.None);
            if (barrier is not null) { _ = await running; throw new IOException("fixture_provider_boundary_not_reached"); }
            if (point is "collected" or "committed" or "retirementAccepted" or "retirementMaterialized")
            {
                if (await running is not null) throw new IOException("fixture_provider_stopped_early");
                await session.CollectStepAsync(stepId);
                if (point is "committed" or "retirementAccepted" or "retirementMaterialized")
                {
                    await session.RestoreStepAsync(stepId);
                    session.FreezeDispatch(ExecutionStopReason.Completed);
                    await session.CollectRemainingAsync(); session.CloseUndispatched(); session.MarkApplied();
                    await session.RestoreRemainingAsync(ExecutionStopReason.Completed);
                    var committed = session.Commit(ExecutionStopReason.Completed); session.Dispose();
                    if (point is "retirementAccepted" or "retirementMaterialized")
                    {
                        new ExecutionRecordProjection(instance, database, new(), lease, new(instance, database, new())).OnCommitted(committed);
                        var retirement = new ContentRetirementService(instance, database, new(), lease, resources, index,
                            new RetirementBarrier(instance, database, accepted.Receipt.JournalId, point, report));
                        _ = retirement.RetireLogsPage(Guid.NewGuid(), "provider-retirement-crash", committed.Facts.RunId)
                            ?? throw new IOException("fixture_retirement_content_missing");
                        throw new IOException("fixture_retirement_boundary_not_reached");
                    }
                }
            }
            else
            {
                var timer = Stopwatch.StartNew();
                while (!File.Exists(marker) && timer.Elapsed < TimeSpan.FromSeconds(30))
                {
                    if (running.IsCompleted) throw new IOException("fixture_provider_not_running");
                    await Task.Delay(20);
                }
                if (!File.Exists(marker)) throw new IOException("fixture_provider_marker_missing");
            }
            Publish(instance, database, accepted.Receipt.JournalId, point!, report);
            await Task.Delay(Timeout.InfiniteTimeSpan); GC.KeepAlive(session); return 0;
        }
        catch (Exception error)
        {
            if (!File.Exists(report)) OwnedFixtureEvidence.WriteReport(report, new { status = "refused", code = error.Message, type = error.GetType().FullName });
            return 1;
        }
    }

    private static void Publish(RuntimeInstance instance, StateDatabase database, Guid parent, string point, string report)
    {
        var state = AttemptWriterState.Read(instance, database, new(), parent);
        var job = state.Steps.Single(); var binding = StepResultCodec.Binding(state.Latest.Root, job);
        var root = new StepEvidenceReader(instance, database, new()).Context(binding).Root
            ?? throw new IOException("fixture_provider_root_missing");
        OwnedFixtureEvidence.WriteReport(report + ".prepared", new { status = "faultReady", point, binding, root,
            journalId = state.Journal.JournalId, attemptId = state.Attempt.AttemptId,
            sqlRevision = state.Journal.Revision, physicalRevision = state.Latest.Revision, ownerProcessId = Environment.ProcessId });
        File.Move(report + ".prepared", report);
    }

    private sealed class RetirementBarrier(RuntimeInstance instance, StateDatabase database, Guid parent, string expected, string report)
        : ICleanupPersistenceObserver
    {
        public void Reached(CleanupPersistencePoint point, Guid journalId, long revision, JournalPhase phase)
        {
            var wanted = expected == "retirementAccepted" ? CleanupPersistencePoint.AcceptanceCommitted : CleanupPersistencePoint.JournalFlushed;
            if (point != wanted || revision != 1 || phase != JournalPhase.Prepared) return;
            Publish(instance, database, parent, expected, report);
            using var wait = new ManualResetEventSlim(); wait.Wait();
        }
    }

    private sealed class Barrier(RuntimeInstance instance, StateDatabase database, string expected, string report) : ITargetPersistenceObserver
    {
        public void Reached(TargetPersistencePoint point, ExecutionTargetBinding binding) { }
        public void Reached(TargetPersistencePoint point, ProviderLauncherBinding binding)
        {
            if (point.ToString() != expected) return;
            Publish(instance, database, binding.ParentJournalId, expected, report);
            using var wait = new ManualResetEventSlim(); wait.Wait();
        }
    }
}
