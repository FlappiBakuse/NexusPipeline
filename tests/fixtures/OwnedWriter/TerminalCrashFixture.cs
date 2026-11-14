using ExecutionTarget = NexusPipeline.Core.Execution.ExecutionTarget;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

[SupportedOSPlatform("windows")]
internal static class TerminalCrashFixture
{
    internal static async Task<int> RunAsync(string[] arguments)
    {
        if (arguments is not [var inputPath, var report]) return 2;
        OwnedFixtureEvidence.RequirePath(inputPath); OwnedFixtureEvidence.RequirePath(report);
        try
        {
            using var input = StrictJson.Parse(File.ReadAllBytes(inputPath)); var value = input.RootElement;
            var runtime = value.GetProperty("runtimeRoot").GetString()!; OwnedFixtureEvidence.RequirePath(runtime);
            var point = value.GetProperty("point").GetString()!;
            if (point is not ("stopped-observed" or "applied" or "terminal-ahead" or "unclosed")) throw new IOException("fixture_terminal_point_invalid");
            var instance = new InstanceRoot(new()).OpenExisting(runtime);
            using var lease = InstanceLease.Acquire(instance, new());
            var database = StateDatabase.OpenExisting(instance, Path.Combine(Path.GetDirectoryName(inputPath)!, "owner-diagnostics"), new JournalStartupAdmission(instance, new()));
            var index = new StartupJournalRecovery(instance, new(), database,
                new(Path.GetDirectoryName(inputPath)!, Path.Combine(runtime, "extensions"))).Recover(lease);
            var resources = new ResourceLeaseManager(new HostLifecycle(index, TimeProvider.System)); var inbox = new ExecutionAdmissionInbox();
            var service = new ExecutionAdmissionService(instance, database, new(), lease, resources, index, inbox, new(5, 6, 7));
            ExecutionTarget target = value.TryGetProperty("queueId", out var queue) ? new QueueTarget(queue.GetGuid()) :
                new BindingTarget(value.GetProperty("bindingId").GetGuid());
            var request = new ExecutionSelectionRequest(target,
                value.GetProperty("catalogRevision").GetInt64(), value.GetProperty("settingsRevision").GetInt64());
            var operation = value.GetProperty("operationId").GetGuid(); var subject = value.GetProperty("subject").GetString()!;
            service.Start(operation, subject, request, DateTimeOffset.Parse("2026-10-21T00:00:00Z"));
            using var accepted = inbox.Take(operation) ?? throw new IOException("fixture_terminal_admission_missing");
            var journalId = accepted.Receipt.JournalId; var stepId = accepted.Prepared.Total.Plan.ReadWire().Steps.First().StepId;
            var scope = new StepWriterScope(instance, database, new(), lease, journalId, stepId);
            using var writer = new ConfigurationWriterProcess(instance, lease, scope);
            var state = scope.VerifyReadOnly(); var binding = StepResultCodec.Binding(state.Latest.Root, state.Steps.Single());
            using var collector = new StepEvidenceCollector(instance, database, new(), lease, binding, writer);
            scope.EnterApplying(); var root = collector.Start();
            if (point == "unclosed")
            {
                OwnedFixtureEvidence.WriteReport(report, new { status = "faultReady", point, journalId, root, binding,
                    ownerProcessId = Environment.ProcessId });
                await Task.Delay(Timeout.InfiniteTimeSpan); return 0;
            }
            if (await writer.AwaitRootAsync(TimeSpan.FromSeconds(10)) != 37) throw new IOException("fixture_terminal_exit_unexpected");
            await collector.ObserveExitAsync();
            var terminal = new AttemptTerminalStore(instance, database, new(), lease);
            if (point == "stopped-observed") terminal.FreezeDispatch(journalId, interrupted: true);
            var stopped = await writer.StopAsync(TimeSpan.FromSeconds(10));
            var collected = await collector.CollectAsync(stopped, TimeSpan.FromSeconds(10));
            if (point != "stopped-observed")
            {
                terminal.RecordCollected(journalId, collected); terminal.FreezeDispatch(journalId, interrupted: point != "terminal-ahead");
                terminal.CloseUndispatched(journalId);
                terminal.MarkApplied(journalId);
            }
            if (point == "terminal-ahead")
            {
                Execute(database, "CREATE TRIGGER records.fixture_terminal_failure BEFORE INSERT ON attempt_receipts BEGIN SELECT RAISE(ABORT,'fixture_terminal_failure'); END;");
                try
                {
                    terminal.Commit(journalId, LifecycleOutcome.Completed, "execution_completed");
                    throw new IOException("fixture_terminal_failure_not_observed");
                }
                catch (SqliteException) { }
                finally { Execute(database, "DROP TRIGGER records.fixture_terminal_failure;"); }
            }
            state = scope.VerifyReadOnly();
            OwnedFixtureEvidence.WriteReport(report, new { status = "faultReady", point, journalId, root, closure = stopped.Closure,
                sqlRevision = state.Journal.Revision, physicalRevision = state.Latest.Revision,
                physicalPhase = state.Latest.Phase.ToString(), binding, ownerProcessId = Environment.ProcessId });
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
        using var connection = NexusPipeline.TestFixtures.DatabaseFixtureConnection.Open(new SqliteConnectionStringBuilder { DataSource = database.DatabasePath, Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
}
