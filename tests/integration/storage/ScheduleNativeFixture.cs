using System.Runtime.Versioning;
using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Scheduling;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class ScheduleNativeFixture : IDisposable
{
    private readonly ExecutionSequentialFixture _sequential = new(accept: false);
    internal ExecutionAdmissionFixture Base => _sequential.Base;
    internal PlanBuilderFixture Source => Base.Source;
    internal Guid ScheduleId { get; } = Guid.NewGuid();
    internal TimeProvider Clock { get; }
    internal DateTimeOffset TriggerUtc { get; }
    internal ScheduleOccurrenceStore Store { get; }
    internal ScheduleRuntimeService Scheduler { get; }
    internal ExecutionRuntimeConsumer Consumer { get; }

    internal ScheduleNativeFixture(TimeProvider? clock = null, bool nextMinute = false)
    {
        Clock = clock ?? TimeProvider.System;
        var now = Clock.GetUtcNow();
        var trigger = nextMinute ? DateTimeOffset.FromUnixTimeSeconds((now.ToUnixTimeSeconds() / 60 + 1) * 60) : now;
        if (nextMinute && trigger - now < TimeSpan.FromSeconds(2)) trigger = trigger.AddMinutes(1);
        TriggerUtc = trigger;
        var catalog = Source.Definitions.Read();
        var queue = catalog.Queues.Single();
        var changes = new AutomationChanges(catalog.Revision, [], [], [], catalog.Bindings
            .Where(binding => binding.ScriptId == queue.ScriptIds[0] && binding.UserId != Source.UserA)
            .Select(binding => binding with { IsEnabled = false }).ToImmutableArray(),
            [queue with { ScriptIds = [queue.ScriptIds[0]], CompletionAction = AutomationCompletionAction.None }],
            [new(ScheduleId, 0, queue.Id, true, "UTC", [(int)trigger.DayOfWeek], trigger.Hour * 60 + trigger.Minute)], []);
        Source.Definitions.Commit(NexusPipeline.Platform.Storage.Automation.AutomationDefinitionStore.Identity(
            Guid.NewGuid(), "schedule-fixture", changes), changes, now);
        Store = new(Source.Instance, Source.Database, Source.Lease, Base.Resources, Clock);
        Scheduler = new(Store,
            new ScheduledExecutionAdmission(Source.Instance, Source.Database, Source.Lease, Base.Service, Clock), Clock);
        Consumer = new(Source.Instance, Source.Database, new(), Source.Lease, Base.Resources, Base.Index, Base.Inbox);
    }

    internal ScheduledOccurrence CommitOnly()
    {
        var checkpoint = Store.ReadCheckpoint();
        var scan = ScheduleMinutePolicy.Scan(Source.Instance.InstanceId, Source.Definitions.Read(), checkpoint.Watermark, Clock.GetUtcNow());
        return Xunit.Assert.Single(Store.CommitMinute(scan));
    }

    internal RunBudgetObservation? Budget(Guid runId)
    {
        using var connection = DatabaseConnections.Open(Source.Database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        return RunBudgetRows.Read(connection, transaction, runId);
    }

    internal void RequireActualClosure(Guid operationId)
    {
        var state = new RecoveryJournalRegistry(Source.Database).ReadSnapshot();
        var journal = Xunit.Assert.Single(state.Journals, item => item.OperationId == operationId);
        var writers = AttemptWriterState.Read(Source.Instance, Source.Database, new(), journal.JournalId);
        var process = Xunit.Assert.Single(writers.Jobs, job => job.Role == "process");
        var closure = new JobClosureStore(Source.Instance, Source.Database, new())
            .ReadForExecutionEvidence(journal.JournalId, process.WriterId, process.JobName, process.SessionId);
        Xunit.Assert.NotNull(closure); Xunit.Assert.True(closure.WasExecuted);
        Xunit.Assert.NotNull(closure.Root); Xunit.Assert.Contains(closure.Root, closure.Family);
        Xunit.Assert.Equal(closure.Family.Length, closure.Family.Select(item => (item.ProcessId, item.CreatedAtUtc)).Distinct().Count());
        foreach (var identity in closure.Family) Xunit.Assert.Equal(WriterState.Stopped,
            WriterStopEvidence.ObserveProcess(identity));
    }

    internal void Sql(string sql)
    {
        using var connection = DatabaseConnections.Open(Source.Database.DatabasePath);
        using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }

    internal long Count(string table)
    {
        using var connection = DatabaseConnections.Open(Source.Database.DatabasePath, readOnly: true);
        using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM " + table + ";";
        return (long)command.ExecuteScalar()!;
    }

    internal IReadOnlyDictionary<string, string> Fingerprints() =>
        new[] { "", "-wal" }.Where(suffix => File.Exists(Source.Database.DatabasePath + suffix)).ToDictionary(
            suffix => suffix, suffix => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Source.Database.DatabasePath + suffix))), StringComparer.Ordinal);

    public void Dispose()
    {
        Scheduler.StopAsync().GetAwaiter().GetResult();
        _sequential.Dispose();
    }

    internal sealed class FrozenScheduleClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
