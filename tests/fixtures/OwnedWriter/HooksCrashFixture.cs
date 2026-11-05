using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

[SupportedOSPlatform("windows")]
internal static class HooksCrashFixture
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is not [var inputPath, var report]) return 2;
        OwnedFixtureEvidence.RequirePath(inputPath); OwnedFixtureEvidence.RequirePath(report);
        try
        {
            using var input = StrictJson.Parse(File.ReadAllBytes(inputPath)); var value = input.RootElement;
            var runtime = value.GetProperty("runtimeRoot").GetString()!; OwnedFixtureEvidence.RequirePath(runtime);
            var point = value.GetProperty("point").GetString();
            if (point is not ("collected" or "hook-result-ahead")) throw new IOException("fixture_hook_crash_point_invalid");
            var instance = new InstanceRoot(new()).OpenExisting(runtime);
            using var lease = InstanceLease.Acquire(instance, new());
            var database = StateDatabase.OpenExisting(instance, Path.Combine(Path.GetDirectoryName(inputPath)!, "owner-diagnostics"),
                new JournalStartupAdmission(instance, new()));
            var index = new StartupJournalRecovery(instance, new(), database,
                new(Path.GetDirectoryName(inputPath)!, Path.Combine(runtime, "extensions"))).Recover(lease);
            var resources = new ResourceLeaseManager(new HostLifecycle(index, TimeProvider.System)); var inbox = new ExecutionAdmissionInbox();
            var service = new ExecutionAdmissionService(instance, database, new(), lease, resources, index, inbox, new(5, 6, 7));
            var operation = value.GetProperty("operationId").GetGuid(); var subject = value.GetProperty("subject").GetString()!;
            service.Start(operation, subject, new(new BindingTarget(value.GetProperty("bindingId").GetGuid()),
                value.GetProperty("catalogRevision").GetInt64(), value.GetProperty("settingsRevision").GetInt64()), DateTimeOffset.UtcNow);
            var accepted = inbox.Take(operation) ?? throw new IOException("fixture_hook_admission_missing");
            var session = new AttemptRuntimeSession(instance, database, new(), lease, resources, index, accepted);
            session.PrepareConfigurationGroups(); var stepId = session.StepIds.Single(); session.PrepareNextStep(stepId);
            var parentId = accepted.Receipt.JournalId;
            if (point == "hook-result-ahead")
            {
                var registered = AttemptWriterState.Read(instance, database, new(), parentId);
                var target = registered.Latest.Revision + 3;
                Execute(database, "CREATE TRIGGER fixture_hook_ahead BEFORE UPDATE OF revision ON operation_journals " +
                    $"WHEN NEW.journal_id='{parentId:D}' AND NEW.revision={target} BEGIN SELECT RAISE(ABORT,'fixture_hook_ahead'); END;");
            }
            session.EnterEffect(stepId);
            try
            {
                if (await session.ExecuteStepAsync(stepId, CancellationToken.None) is not null) throw new IOException("fixture_hook_stopped_early");
                await session.CollectStepAsync(stepId);
                if (point == "hook-result-ahead") throw new IOException("fixture_hook_failure_not_observed");
            }
            catch (SqliteException) when (point == "hook-result-ahead") { }
            finally { if (point == "hook-result-ahead") Execute(database, "DROP TRIGGER fixture_hook_ahead;"); }
            var state = AttemptWriterState.Read(instance, database, new(), parentId);
            OwnedFixtureEvidence.WriteReport(report, new { status = "faultReady", point, journalId = parentId,
                attemptId = state.Attempt.AttemptId, state.Jobs, sqlRevision = state.Journal.Revision, physicalRevision = state.Latest.Revision,
                ownerProcessId = Environment.ProcessId });
            await Task.Delay(Timeout.InfiniteTimeSpan); return 0;
        }
        catch (Exception error)
        {
            if (!File.Exists(report)) OwnedFixtureEvidence.WriteReport(report, new { status = "refused", code = error.Message, type = error.GetType().FullName });
            return 1;
        }
    }

    private static void Execute(StateDatabase database, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database.DatabasePath, Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
}
