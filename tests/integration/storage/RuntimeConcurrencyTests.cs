using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class RuntimeConcurrencyTests
{
    [Fact(DisplayName = "Normal Startup Leaves Completed History Unread And Explicit Access Or Diagnosis Rejects Its Damaged Bytes")]
    public async Task LoadsHistoryOnDemand()
    {
        using var fixture = new ExecutionRuntimeFixture();
        _ = await fixture.Consumer.RunNextAsync(); var source = fixture.Source;
        VerifyHistoricalClosures(source.Instance, source.Database);
        var roots = new NexusPipeline.Platform.Storage.Journals.PayloadApplyRoots(Path.Combine(source.Root, "host"), Path.Combine(source.Instance.RuntimeRoot, "extensions"));
        Assert.True(new NexusPipeline.Platform.Storage.Journals.StartupJournalRecovery(source.Instance, new(), source.Database, roots).Recover(source.Lease).Current.IsReady);
        Assert.Empty(new RecoveryJournalRegistry(source.Database).ReadActiveSnapshot().Journals);
        var state = fixture.State;
        var planPath = Path.Combine(source.Instance.RuntimeRoot, state.Journal.PlanPath);
        var originalPlan = File.ReadAllBytes(planPath);
        var plan = System.Text.Json.Nodes.JsonNode.Parse(originalPlan)!; plan["version"] = 999;
        var unknownPlan = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(plan); File.WriteAllBytes(planPath, unknownPlan);
        Assert.True(new NexusPipeline.Platform.Storage.Journals.StartupJournalRecovery(source.Instance, new(), source.Database, roots).Recover(source.Lease).Current.IsReady);
        Assert.Equal(unknownPlan, File.ReadAllBytes(planPath));
        Assert.Equal("journal_plan_digest_mismatch", Assert.Throws<IOException>(() =>
            AttemptWriterState.Read(source.Instance, source.Database, new(), state.Journal.JournalId)).Message);
        File.WriteAllBytes(planPath, originalPlan);
        var path = Path.Combine(source.Instance.RuntimeRoot, "journals", state.Journal.JournalId.ToString("D"), state.Journal.Revision.ToString("D10") + ".json");
        var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(path))!; document["version"] = 999;
        var unknown = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(document); File.WriteAllBytes(path, unknown);
        var current = StateDatabase.OpenCurrent(source.Instance, source.Lease);
        Assert.True(new NexusPipeline.Platform.Storage.Journals.StartupJournalRecovery(source.Instance, new(), current, roots).Recover(source.Lease).Current.IsReady);
        Assert.Equal("unsupported_or_invalid_contract", Assert.Throws<JsonException>(() =>
            AttemptWriterState.Read(source.Instance, current, new(), state.Journal.JournalId)).Message);
        source.Lease.Dispose();
        Assert.Equal("unsupported_or_invalid_contract", Assert.Throws<JsonException>(() =>
            StateDatabase.OpenExisting(source.Instance, Path.Combine(source.Root, "explicit-inspection"),
                new NexusPipeline.Platform.Storage.Journals.JournalStartupAdmission(source.Instance, new(), roots))).Message);
        Assert.Equal(unknown, File.ReadAllBytes(path));
    }

    [Fact(DisplayName = "Independent Admitted Runs Reach Their Real Writers Together And One Cancellation Leaves The Other Running")]
    public async Task RunsIndependentOwners()
    {
        using var fixture = new ExecutionAdmissionFixture(); var source = fixture.Source;
        var catalog = source.Definitions.Read();
        var native = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!;
        var root = new AutomationRoot(Guid.NewGuid(), 0, "Concurrent native", AutomationRootKind.Script, Path.GetDirectoryName(native)!);
        var markers = catalog.Scripts.ToDictionary(script => script.Id, script => Path.Combine(source.Root, script.Id.ToString("N")));
        var changes = new AutomationChanges(catalog.Revision, [root], [.. catalog.Scripts.Select(script => script with
        {
            RootId = root.Id, Executable = new(root.Id, Path.GetFileName(native)),
            ConfigurationPaths = [], LogPaths = [], Arguments = ["process-output", "wait", markers[script.Id]],
            Limits = new(1, 0, 0)
        })], [], [], [], [], []);
        source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "concurrent-fixture", changes), changes, DateTimeOffset.UtcNow);
        var first = fixture.Service.Start(Guid.NewGuid(), "concurrent-fixture", source.Request(new BindingTarget(source.BindingA)), DateTimeOffset.UtcNow);
        var second = fixture.Service.Start(Guid.NewGuid(), "concurrent-fixture", source.Request(new BindingTarget(source.OtherBinding)), DateTimeOffset.UtcNow);
        Assert.Throws<ExecutionSelectionException>(() => fixture.Service.Start(Guid.NewGuid(), "concurrent-fixture",
            source.Request(new BindingTarget(source.BindingA)), DateTimeOffset.UtcNow));
        var consumer = new ExecutionRuntimeConsumer(source.Instance, source.Database, new(), source.Lease, fixture.Resources, fixture.Index, fixture.Inbox);
        using var stopping = new CancellationTokenSource();
        var running = consumer.RunAsync(stopping.Token);
        try
        {
            await Until(() => markers.Values.All(marker => File.Exists(marker + ".output-ready")), running);
            var firstRoot = ReadRunningRoot(markers[source.ScriptId]);
            var secondRoot = ReadRunningRoot(markers[source.OtherScriptId]);
            Assert.NotEqual(OwnedWorkerRoot.From(firstRoot), OwnedWorkerRoot.From(secondRoot));
            Cancel(first);
            await Until(() => fixture.Count("attempt_receipts") == 1, running);
            Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(firstRoot));
            Assert.Equal(WriterState.Running, WriterStopEvidence.ObserveProcess(secondRoot));
            var secondState = AttemptWriterState.Read(source.Instance, source.Database, new(), second.JournalId);
            Assert.NotEqual(NexusPipeline.Core.Configuration.JournalPhase.Committed, secondState.Journal.Phase);
            Assert.Single(secondState.Steps);
            Assert.Equal(0, consumer.PendingReconciliationCount);
            Cancel(second);
            await Until(() => fixture.Count("attempt_receipts") == 2, running);
            stopping.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(fixture.Index.Current.IsReady); Assert.True(consumer.FailureCode is null, consumer.FailureException?.ToString());
            var closures = new JobClosureStore(source.Instance, source.Database, new());
            foreach (var (accepted, observed) in new[] { (first, firstRoot), (second, secondRoot) })
            {
                var state = AttemptWriterState.Read(source.Instance, source.Database, new(), accepted.JournalId);
                var receipt = Assert.IsType<AttemptReceipt>(new AttemptReceiptStore(source.Database).Read(Guid.Parse(state.Attempt.AttemptId)));
                Assert.Equal(LifecycleOutcome.Cancelled, receipt.Facts.Lifecycle);
                Assert.Equal(accepted.Acceptance.OperationId, receipt.OperationId);
                var job = Assert.Single(state.AllJobs);
                Assert.Equal(Assert.Single(state.Total.Steps).StepId, job.StepId);
                _ = OwnedWorkerRoot.From(observed).RequireStopped(closures, receipt.JournalId, job);
            }
            ExecutionCancellationStore.VerifyInventory(source.Instance, source.Database, new(), pageSize: 1);
            var original = source.Database.Write((connection, transaction) =>
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "SELECT request_digest FROM main.operations WHERE operation_kind='execution.cancel' ORDER BY initial_journal_id DESC LIMIT 1;";
                var digest = (string)command.ExecuteScalar()!;
                command.CommandText = "UPDATE main.operations SET request_digest=$digest WHERE operation_id=(SELECT operation_id FROM main.operations WHERE operation_kind='execution.cancel' ORDER BY initial_journal_id DESC LIMIT 1);";
                command.Parameters.AddWithValue("$digest", new string('0', 64)); command.ExecuteNonQuery(); return digest;
            });
            Assert.Equal("execution_cancel_operation_unbound", Assert.Throws<IOException>(() =>
                ExecutionCancellationStore.VerifyInventory(source.Instance, source.Database, new(), pageSize: 1)).Message);
            source.Database.Write((connection, transaction) =>
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "UPDATE main.operations SET request_digest=$digest WHERE operation_id=(SELECT operation_id FROM main.operations WHERE operation_kind='execution.cancel' ORDER BY initial_journal_id DESC LIMIT 1);";
                command.Parameters.AddWithValue("$digest", original); return command.ExecuteNonQuery();
            });
            ExecutionCancellationStore.VerifyInventory(source.Instance, source.Database, new(), pageSize: 1);
        }
        finally { stopping.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(30)); }

        void Cancel(AttemptAcceptanceResult accepted)
        {
            var state = AttemptWriterState.Read(source.Instance, source.Database, new(), accepted.JournalId);
            new ExecutionCancellationStore(source.Instance, source.Database, new(), source.Lease).Cancel(
                ExecutionCancellationStore.Identity(Guid.NewGuid(), "concurrent-fixture", accepted.JournalId, state.Journal.OperationId,
                    Guid.Parse(state.Attempt.RunId), Guid.Parse(state.Attempt.AttemptId)), accepted.JournalId, DateTimeOffset.UtcNow);
        }
    }

    private static ProcessIdentity ReadRunningRoot(string marker)
    {
        var id = int.Parse(File.ReadAllText(marker + ".output-ready"), System.Globalization.CultureInfo.InvariantCulture);
        var root = ProcessIdentityReader.Read(id);
        Assert.Equal(WriterState.Running, WriterStopEvidence.ObserveProcess(root));
        return root;
    }

    private static void VerifyHistoricalClosures(NexusPipeline.Platform.Storage.RuntimeInstance instance, StateDatabase database)
    {
        JobClosureStore.VerifyInventory(instance, database, new(), pageSize: 1);
        var original = database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT plan_digest FROM main.journal_writer_job_closures ORDER BY journal_id DESC,writer_id DESC,job_name DESC LIMIT 1;";
            var digest = (string)command.ExecuteScalar()!;
            command.CommandText = "UPDATE main.journal_writer_job_closures SET plan_digest=$digest WHERE (journal_id,writer_id,job_name)=(SELECT journal_id,writer_id,job_name FROM main.journal_writer_job_closures ORDER BY journal_id DESC,writer_id DESC,job_name DESC LIMIT 1);";
            command.Parameters.AddWithValue("$digest", new string('0', 64));
            Assert.Equal(1, command.ExecuteNonQuery());
            return digest;
        });
        Assert.Equal("writer_job_closure_reference_invalid", Assert.Throws<IOException>(() =>
            JobClosureStore.VerifyInventory(instance, database, new(), pageSize: 1)).Message);
        database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE main.journal_writer_job_closures SET plan_digest=$digest WHERE (journal_id,writer_id,job_name)=(SELECT journal_id,writer_id,job_name FROM main.journal_writer_job_closures ORDER BY journal_id DESC,writer_id DESC,job_name DESC LIMIT 1);";
            command.Parameters.AddWithValue("$digest", original); return command.ExecuteNonQuery();
        });
        JobClosureStore.VerifyInventory(instance, database, new(), pageSize: 1);
    }

    private static async Task Until(Func<bool> condition, Task consumer)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && !consumer.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(20);
        Assert.True(condition(), "Actual concurrent runtime did not reach the expected boundary");
    }
}
