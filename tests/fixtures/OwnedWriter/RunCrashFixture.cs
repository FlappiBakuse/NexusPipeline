using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

internal static class RunCrashFixture
{
    internal static async Task<int> RunAsync(string[] arguments)
    {
        if (arguments is not [var inputPath, var report]) return 2;
        OwnedFixtureEvidence.RequirePath(inputPath); OwnedFixtureEvidence.RequirePath(report);
        try
        {
            using var document = StrictJson.Parse(File.ReadAllBytes(inputPath)); var input = document.RootElement;
            var runtime = input.GetProperty("runtimeRoot").GetString()!; OwnedFixtureEvidence.RequirePath(runtime);
            var nativeReport = input.GetProperty("nativeReport").GetString()!; OwnedFixtureEvidence.RequirePath(nativeReport);
            var point = input.GetProperty("point").GetString()!;
            if (point is not ("sealed" or "restored" or "no-job" or "intent-synced" or "intent-ahead" or "intent-fulfilled"))
                throw new IOException("fixture_run_point_invalid");
            var request = input.GetProperty("request").Deserialize<ConfigurationRunRequest>()!;
            foreach (var resource in request.Resources) OwnedFixtureEvidence.RequirePath(resource.Target.TargetPath);
            var parent = input.GetProperty("parent").Deserialize<RunAttemptPlan>()!;
            var sessionId = input.GetProperty("sessionId").GetGuid(); var subject = input.GetProperty("subject").GetString()!;
            var owner = Guid.Parse(parent.OperationId); var instance = new InstanceRoot(new()).OpenExisting(runtime);
            using var lease = InstanceLease.Acquire(instance, new());
            var database = StateDatabase.OpenExisting(instance, Path.Combine(Path.GetDirectoryName(inputPath)!, "run-owner-diagnostics"),
                new JournalStartupAdmission(instance, new()));
            var index = new StartupJournalRecovery(instance, new(), database,
                new(Path.GetDirectoryName(inputPath)!, Path.Combine(runtime, "extensions"))).Recover(lease);
            var resources = new ResourceLeaseManager(new HostLifecycle(index, TimeProvider.System));
            var codec = new ExecutionPlanCodec(instance, new(), new(instance, new()));
            var reference = parent.ExecutionPlan;
            using var execution = codec.Freeze(codec.Read(new(reference.RelativePath, reference.SizeBytes, reference.Sha256)).ReadWire());
            var preparation = new ConfigurationRunPreparation(instance, database, new(), lease, resources);
            var stepId = execution.Plan.ReadWire().Steps[0].StepId;
            using var root = resources.TryAcquire(owner, preparation.Claims(execution.Plan, stepId, request)).Lease
                ?? throw new IOException("fixture_run_claims_rejected");
            using var prepared = preparation.Prepare(root, owner, execution.Plan, stepId, request);
            var children = prepared.Use(items => items.Select(item => item.Reference).OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToArray());
            var expected = parent.ConfigurationPlans.Where(item => item.StepId == stepId)
                .Select(item => new BlobReference(item.Plan.RelativePath, item.Plan.SizeBytes, item.Plan.Sha256))
                .OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToArray();
            if (!children.SequenceEqual(expected)) throw new IOException("fixture_run_child_references_changed");
            new RunAttemptAcceptance(instance, new(), database).Accept(new(owner, subject, new('b', 64), "fixture.run"), parent, DateTimeOffset.UtcNow);
            var store = new ConfigurationRunStore(instance, database, new()); var parentId = Guid.Parse(parent.JournalId);
            var session = store.Begin(ConfigurationRunStore.BeginIdentity(sessionId, subject, parentId, stepId, prepared), parentId, stepId, prepared, DateTimeOffset.UtcNow);
            if (point.StartsWith("intent-", StringComparison.Ordinal))
            {
                prepared.Dispose(); root.Dispose();
                return await PrepareIntentAsync(instance, database, lease, resources, index, store, session, subject, point, report, children,
                    input.GetProperty("completionOperationId").GetGuid());
            }
            if (point == "no-job") return await PauseAsync(report, point, session, children, 0, 0);
            return await ExecuteAsync(instance, database, lease, store, session, subject, point, nativeReport, report, children);
        }
        catch (Exception error)
        {
            if (!File.Exists(report)) OwnedFixtureEvidence.WriteReport(report, new { status = "refused", code = error.Message, type = error.GetType().FullName });
            return 1;
        }
    }

    private static async Task<int> PrepareIntentAsync(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
        ResourceLeaseManager resources, RecoveryIndex index, ConfigurationRunStore store, ConfigurationRunStatus session,
        string subject, string point, string report, BlobReference[] children, Guid operationId)
    {
        var registry = new RecoveryJournalRegistry(database);
        var parent = registry.ReadSnapshot().Journals.Single(item => item.JournalId == session.ParentJournalId);
        new JournalEffectGate(database).SealUnentered(parent, 1);
        var exchange = new ConfigurationEditExchange(instance, database, new());
        foreach (var resource in session.Resources)
            if (exchange.Restore(resource.JournalId) != ConfigurationRecoveryResult.NotExecuted)
                throw new IOException("fixture_run_no_job_restoration_failed");
        new RecoveryCoordinator(registry, new JournalRecoveryEvidence(instance, new(instance, new())), index).VerifyAndPublish();
        ConfigurationRecoveryHolds.Publish(database, index);
        var completion = new RunCompletionService(instance, database, new(), lease, resources, index);
        if (point == "intent-fulfilled")
        {
            var result = completion.CompleteInterrupted(operationId, subject, session.SessionId);
            if (!result.IsNew || result.Session.Phase != ConfigurationRunPhase.Interrupted)
                throw new IOException("fixture_run_completion_failed");
        }
        else
        {
            Execute(database, point == "intent-ahead" ? "CREATE TRIGGER fixture_intent_failure BEFORE UPDATE ON operation_journals " +
                $"WHEN OLD.journal_id='{session.ParentJournalId:D}' BEGIN SELECT RAISE(ABORT,'fixture_intent_failure'); END;" :
                "CREATE TRIGGER fixture_intent_failure BEFORE INSERT ON configuration_run_sync_items BEGIN SELECT RAISE(ABORT,'fixture_intent_failure'); END;");
            try
            {
                try
                {
                    completion.CompleteInterrupted(operationId, subject, session.SessionId);
                    throw new IOException("fixture_run_intent_failure_not_observed");
                }
                catch (SqliteException error) when (error.Message.Contains("fixture_intent_failure", StringComparison.Ordinal)) { }
            }
            finally { Execute(database, "DROP TRIGGER fixture_intent_failure;"); }
        }
        var physical = new JournalStore(instance, new()).Read(session.ParentJournalId);
        var sqlParent = registry.ReadSnapshot().Journals.Single(item => item.JournalId == session.ParentJournalId);
        session = store.Read(session.SessionId, subject)!;
        if (physical.Revision != 2 || sqlParent.Revision != (point == "intent-ahead" ? 1 : 2) ||
            session.IsWriterAdmissionClosed != true || session.ClosedElapsedMilliseconds != 0)
            throw new IOException("fixture_run_intent_boundary_unbound");
        OwnedFixtureEvidence.WriteReport(report, new { status = "faultReady", point, processId = Environment.ProcessId,
            sessionId = session.SessionId, completionOperationId = operationId, physicalParentRevision = physical.Revision,
            sqlParentRevision = sqlParent.Revision, fulfilled = session.CompletionOperationId.HasValue, children });
        await Task.Delay(Timeout.InfiniteTimeSpan); return 0;
    }

    private static void Execute(StateDatabase database, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database.DatabasePath, Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }

    private static async Task<int> ExecuteAsync(RuntimeInstance instance, StateDatabase database, InstanceLease lease, ConfigurationRunStore store,
        ConfigurationRunStatus session, string subject, string point, string nativeReport, string report, BlobReference[] children)
    {
        using var writer = new RunWriterProcess(instance, database, new(), lease, session.SessionId, subject);
        var exchange = new ConfigurationEditExchange(instance, database, new());
        foreach (var resource in session.Resources) exchange.Install(resource.JournalId);
        store.MarkRunning(session.SessionId, subject, 1);
        var root = writer.Start(); var ready = await WaitNativeAsync(nativeReport);
        if (root.ProcessId != ready.GetProperty("processId").GetInt32()) throw new IOException("fixture_run_root_unbound");
        var child = (await WaitNativeAsync(nativeReport + ".child")).GetProperty("processId").GetInt32();
        if (child == root.ProcessId) throw new IOException("fixture_run_child_unbound");
        await Task.Delay(TimeSpan.FromSeconds(15));
        var stopped = await writer.StopAsync(TimeSpan.FromSeconds(10));
        if (!stopped.WasExecuted) throw new IOException("fixture_run_was_not_executed");
        session = store.Read(session.SessionId, subject)!;
        if (session.ClosedElapsedMilliseconds is not >= 15000) throw new IOException("fixture_run_duration_not_due");
        var recorder = new EditOutputRecorder(instance, database, new(), lease);
        foreach (var resource in session.Resources) recorder.Seal(resource.JournalId, stopped);
        if (point == "restored")
            foreach (var resource in session.Resources)
                if (exchange.Restore(resource.JournalId) != ConfigurationRecoveryResult.Restored) throw new IOException("fixture_run_restoration_failed");
        return await PauseAsync(report, point, session, children, root.ProcessId, child);
    }

    private static async Task<JsonElement> WaitNativeAsync(string path)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (File.Exists(path))
            {
                try { using var document = JsonDocument.Parse(File.ReadAllBytes(path)); return document.RootElement.Clone(); }
                catch (Exception error) when (error is IOException or JsonException) { }
            }
            await Task.Delay(20);
        }
        throw new IOException("fixture_run_writer_not_ready");
    }

    private static async Task<int> PauseAsync(string report, string point, ConfigurationRunStatus session, BlobReference[] children, int rootProcessId, int childProcessId)
    {
        OwnedFixtureEvidence.WriteReport(report, new { status = "faultReady", point, processId = Environment.ProcessId,
            sessionId = session.SessionId, elapsedMilliseconds = session.ClosedElapsedMilliseconds, rootProcessId, childProcessId, children });
        await Task.Delay(Timeout.InfiniteTimeSpan); return 0;
    }
}
