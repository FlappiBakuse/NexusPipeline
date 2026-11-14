using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Media;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Records;
using NexusPipeline.Platform.Storage.Sqlite;

[SupportedOSPlatform("windows")]
internal static class TargetCrashFixture
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is not [var inputPath, var report]) return 2;
        OwnedFixtureEvidence.RequirePath(inputPath); OwnedFixtureEvidence.RequirePath(report);
        try
        {
            using var input = StrictJson.Parse(File.ReadAllBytes(inputPath)); var value = input.RootElement;
            var runtime = value.GetProperty("runtimeRoot").GetString()!; OwnedFixtureEvidence.RequirePath(runtime);
            var point = value.GetProperty("point").GetString()!;
            if (point is not ("LivePreviewOpen" or "RootAdmitted" or "RetentionPrepared" or "HandoffApplied" or "retained" or "stage-ahead" or "AndroidCollectedPublished" or "CaptureSourcePublished" or "CaptureRootPublished" or "CaptureParentStopped" or "CaptureCollectedAppended" or "CaptureCommandRootPublished" or "CaptureCommandObservedPublished" or "ImageRetirementAccepted" or "ImageRetirementMaterialized"))
                throw new IOException("fixture_target_crash_point_invalid");
            var instance = new InstanceRoot(new()).OpenExisting(runtime);
            using var lease = InstanceLease.Acquire(instance, new());
            var database = StateDatabase.OpenExisting(instance, Path.Combine(Path.GetDirectoryName(inputPath)!, "owner-diagnostics"),
                new JournalStartupAdmission(instance, new()));
            var index = new StartupJournalRecovery(instance, new(), database,
                new(Path.GetDirectoryName(inputPath)!, Path.Combine(runtime, "extensions"))).Recover(lease);
            var resources = new ResourceLeaseManager(new HostLifecycle(index, TimeProvider.System)); var inbox = new ExecutionAdmissionInbox();
            var imageRetirement = point.StartsWith("ImageRetirement", StringComparison.Ordinal);
            var capture = point == "LivePreviewOpen" || imageRetirement || point.StartsWith("Capture", StringComparison.Ordinal) ||
                value.TryGetProperty("capture", out var captureValue) && captureValue.ValueKind == JsonValueKind.True;
            var service = new ExecutionAdmissionService(instance, database, new(), lease, resources, index, inbox, new(5, 6, 7),
                judgementRuntime: capture ? new(Environment.GetEnvironmentVariable("NEXUS_TEST_HOST_EXE")!, null) : null);
            var operation = value.GetProperty("operationId").GetGuid();
            var request = new ExecutionSelectionRequest(new BindingTarget(value.GetProperty("bindingId").GetGuid()),
                value.GetProperty("catalogRevision").GetInt64(), value.GetProperty("settingsRevision").GetInt64());
            if (value.TryGetProperty("mumuToolPath", out var mumuTool) && mumuTool.ValueKind == JsonValueKind.String)
            {
                var tool = mumuTool.GetString()!; OwnedFixtureEvidence.RequirePath(tool);
                using var discovery = PreparedMuMuDiscovery.Prepare(instance, new(), lease, resources, tool,
                    value.GetProperty("mumuEndpoint").GetString()!);
                await discovery.DiscoverAsync(TimeSpan.FromSeconds(15));
                service.StartWithDiscoveries(operation, value.GetProperty("subject").GetString()!, request,
                    DateTimeOffset.UtcNow, new Dictionary<Guid, PreparedMuMuDiscovery> {
                        [value.GetProperty("mumuScriptId").GetGuid()] = discovery });
            }
            else service.Start(operation, value.GetProperty("subject").GetString()!, request, DateTimeOffset.UtcNow);
            var accepted = inbox.Take(operation) ?? throw new IOException("fixture_target_admission_missing");
            var observer = new Barrier(instance, database, point, report);
            var liveMedia = point == "LivePreviewOpen" ? new ExecutionLiveMedia(instance.InstanceId) : null;
            var session = new AttemptRuntimeSession(instance, database, new(), lease, resources, index, accepted, observer, liveMedia);
            session.PrepareConfigurationGroups(); var stepId = session.StepIds.Single(); session.PrepareNextStep(stepId);
            var parent = accepted.Receipt.JournalId;
            session.EnterEffect(stepId);
            if (point == "LivePreviewOpen")
            {
                var activation = new ExtensionActivation("crash-live-fixture");
                using var registration = liveMedia!.Previews.Register(activation, "window", liveMedia); activation.Publish();
                var executing = session.ExecuteStepAsync(stepId, CancellationToken.None);
                var previewState = AttemptWriterState.Read(instance, database, new(), parent);
                var context = new RunMediaContext(instance.InstanceId, Guid.Parse(previewState.Attempt.RunId), Guid.Parse(previewState.Attempt.AttemptId));
                var clock = System.Diagnostics.Stopwatch.StartNew(); ExecutionLivePreview? preview = null;
                while (preview is null)
                {
                    if (executing.IsCompleted) throw new IOException("fixture_live_main_ended_early");
                    try { preview = liveMedia.OpenPreview(accepted.Identity.SubjectId, context, "crash-live-fixture", "window"); }
                    catch (UnauthorizedAccessException) when (clock.Elapsed < TimeSpan.FromSeconds(15)) { await Task.Delay(20); }
                }
                _ = await preview.CaptureAsync(); await preview.DisposeAsync();
                previewState = AttemptWriterState.Read(instance, database, new(), parent);
                var previewTarget = TargetJobCodec.Read(previewState.Latest.Root, previewState.Total, previewState.Attempt.ExecutionPlan.Sha256).Single();
                observer.Publish(previewTarget.Binding);
                await Task.Delay(Timeout.InfiniteTimeSpan); GC.KeepAlive(session); GC.KeepAlive(executing); return 0;
            }
            if (point == "stage-ahead")
            {
                var registered = AttemptWriterState.Read(instance, database, new(), parent);
                Execute(database, "CREATE TRIGGER main.fixture_target_ahead BEFORE UPDATE OF revision ON operation_journals " +
                    $"WHEN NEW.journal_id='{parent:D}' AND NEW.revision={registered.Latest.Revision + 4} " +
                    "BEGIN SELECT RAISE(ABORT,'fixture_target_ahead'); END;");
            }
            try
            {
                if (await session.ExecuteStepAsync(stepId, CancellationToken.None) is not null) throw new IOException("fixture_target_stopped_early");
                await session.CollectStepAsync(stepId);
                if (point == "stage-ahead") throw new IOException("fixture_target_failure_not_observed");
            }
            catch (SqliteException error) when (point == "stage-ahead" && error.Message.Contains("fixture_target_ahead", StringComparison.Ordinal)) { }
            finally { if (point == "stage-ahead") Execute(database, "DROP TRIGGER main.fixture_target_ahead;"); }
            if (imageRetirement)
            {
                await session.RestoreStepAsync(stepId);
                session.FreezeDispatch(ExecutionStopReason.Completed);
                await session.CollectRemainingAsync(); session.CloseUndispatched(); session.MarkApplied();
                await session.RestoreRemainingAsync(ExecutionStopReason.Completed);
                var committed = session.Commit(ExecutionStopReason.Completed); session.Dispose();
                new ExecutionRecordProjection(instance, database, new(), lease, new(instance, database, new())).OnCommitted(committed);
                var completed = AttemptWriterState.Read(instance, database, new(), parent);
                var binding = TargetJobCodec.Read(completed.Latest.Root, completed.Total,
                    completed.Attempt.ExecutionPlan.Sha256).Single().Binding;
                var retirement = new ContentRetirementService(instance, database, new(), lease, resources, index,
                    new ImageRetirementBarrier(() => observer.Publish(binding), point));
                _ = retirement.RetireImagesPage(Guid.NewGuid(), "image-retirement-crash", committed.Facts.RunId)
                    ?? throw new IOException("fixture_image_retirement_content_missing");
                throw new IOException("fixture_image_retirement_boundary_not_reached");
            }
            var state = AttemptWriterState.Read(instance, database, new(), parent);
            var target = TargetJobCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256).Single();
            observer.Publish(target.Binding);
            await Task.Delay(Timeout.InfiniteTimeSpan); GC.KeepAlive(session); return 0;
        }
        catch (Exception error)
        {
            if (!File.Exists(report)) Publish(report, new { status = "refused", code = error.Message, type = error.GetType().FullName });
            return 1;
        }
    }

    private sealed class Barrier(RuntimeInstance instance, StateDatabase database, string expected, string report) : ITargetPersistenceObserver
    {
        public void Reached(TargetPersistencePoint point, ExecutionTargetBinding binding)
        {
            if (point.ToString() != expected) return;
            Publish(binding);
            using var wait = new ManualResetEventSlim(); wait.Wait();
        }

        public void Reached(TargetPersistencePoint point, ExecutionCaptureBinding binding)
        {
            if (point.ToString() != expected) return;
            var state = AttemptWriterState.Read(instance, database, new(), binding.Primary.ParentJournalId);
            var android = state.Total.Steps.Single(item => item.StepId == binding.Primary.StepId).Target?.Kind == "android";
            var target = android ? AndroidControlCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)
                .Last(item => item.Binding.StepId == binding.Primary.StepId).Binding :
                TargetJobCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256).Single().Binding;
            Publish(target);
            using var wait = new ManualResetEventSlim(); wait.Wait();
        }

        internal void Publish(ExecutionTargetBinding binding)
        {
            var state = AttemptWriterState.Read(instance, database, new(), binding.ParentJournalId);
            var step = state.Total.Steps.Single(item => item.StepId == binding.StepId);
            var root = step.Target?.Kind == "android" ? TargetRootFacts.ReadCommand(database,
                AndroidControlCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256).Single(item => item.Binding == binding)) :
                TargetRootFacts.Read(database, binding);
            if (root is null) throw new IOException("fixture_target_root_missing");
            TargetCrashFixture.Publish(report, new { status = "faultReady", point = expected, binding, root,
                journalId = binding.ParentJournalId, attemptId = state.Attempt.AttemptId, sqlRevision = state.Journal.Revision,
                physicalRevision = state.Latest.Revision, ownerProcessId = Environment.ProcessId });
        }
    }

    private sealed class ImageRetirementBarrier(Action publish, string expected) : ICleanupPersistenceObserver
    {
        public void Reached(CleanupPersistencePoint point, Guid journalId, long revision, JournalPhase phase)
        {
            var wanted = expected == "ImageRetirementAccepted" ? CleanupPersistencePoint.AcceptanceCommitted :
                CleanupPersistencePoint.JournalFlushed;
            if (point != wanted || revision != 1 || phase != JournalPhase.Prepared) return;
            publish(); using var wait = new ManualResetEventSlim(); wait.Wait();
        }
    }

    private static void Publish(string report, object value)
    {
        OwnedFixtureEvidence.WriteReport(report + ".prepared", value); File.Move(report + ".prepared", report);
    }

    private static void Execute(StateDatabase database, string sql)
    {
        using var connection = NexusPipeline.TestFixtures.DatabaseFixtureConnection.Open(new SqliteConnectionStringBuilder { DataSource = database.DatabasePath, Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
}
