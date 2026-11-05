using System.Runtime.Versioning;
using NexusPipeline.Core.Records;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Records;

public sealed record RecordAttemptDisplay(Guid AttemptId, DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt, long? DurationMilliseconds);
public sealed record RecordDisplay(Guid RunId, string TargetName, string? UserName, string? Mode,
    long MaximumAttempts, DateTimeOffset? StartedAt, IReadOnlyList<RecordAttemptDisplay> Attempts)
{
    public IReadOnlyList<RecordAccountDisplay> Accounts { get; init; } = [];
    public IReadOnlyList<RecordStepDisplay> Steps { get; init; } = [];
}
public sealed record RecordStepDisplay(string StepId, string ScriptName);
public sealed record RecordAccountDisplay(Guid UserId, string Name, IReadOnlyList<string> StepIds);

[SupportedOSPlatform("windows")]
public sealed class RecordDisplayReader(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    public RecordDisplay? Read(Guid runId)
    {
        var record = new RecordSummaryReader(database).Read(runId);
        if (record is null) return null;
        var receipts = new AttemptHistoryReader(database).ReadPage(new(runId, Limit: 100)).Items;
        if (receipts.Count == 0) return null;
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        var first = receipts[0];
        var journal = RecoveryJournalRegistry.ReadInside(connection, transaction, first.JournalId);
        var metadata = AttemptPreparedBinding.ReadPlanMetadata(instance, contracts, journal);
        var total = metadata.Total;
        if (metadata.Plan.RunId != runId.ToString("D") || metadata.Plan.AttemptId != first.Facts.AttemptId.ToString("D") ||
            metadata.Plan.AttemptOrdinal != first.Facts.Ordinal || journal.PlanDigest != first.PlanDigest)
            throw new IOException("record_display_source_unbound");
        var reference = total.DefinitionBundle;
        if (reference.SizeBytes is < 1 or > StrictJson.MaximumBytes) throw new IOException("record_display_source_unbound");
        using var stream = new ImmutableBlobStore(instance, contracts).OpenVerified(new(reference.RelativePath,
            reference.SizeBytes, reference.Sha256));
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        var bundle = contracts.Read<AutomationBundle>(bytes, "automation-bundle.schema.json");
        var target = total.QueueId is null
            ? bundle.Scripts.SingleOrDefault(item => item.Id == record.AutomationId.ToString("D"))?.Name
            : total.QueueId == record.AutomationId.ToString("D")
                ? bundle.Queues.SingleOrDefault(item => item.Id == total.QueueId)?.Name : null;
        if (target is null) throw new IOException("record_display_source_unbound");
        var user = record.UserId is { } userId
            ? bundle.Users.SingleOrDefault(item => item.Id == userId.ToString("D"))?.Name : null;
        if (record.UserId.HasValue && user is null) throw new IOException("record_display_source_unbound");
        var observations = new List<RunBudgetObservation>();
        var budget = RunBudgetRows.Read(connection, transaction, runId, observations.Add);
        var timings = new List<RecordAttemptDisplay>();
        long? previousConsumed = 0;
        foreach (var receipt in receipts)
        {
            var saved = observations.Where(item => item.JournalId == receipt.JournalId).ToArray();
            var terminal = saved.SingleOrDefault(item => item.TerminalReceiptDigest == receipt.SourceReceiptDigest);
            var start = receipt.Facts.Ordinal == 1 ? budget?.FirstPreparedAtUtc
                : saved.FirstOrDefault(item => item.TerminalReceiptDigest is null)?.ObservedAtUtc;
            var duration = terminal is { ClockRegressed: false } && previousConsumed.HasValue
                ? terminal.ConsumedMilliseconds - previousConsumed : null;
            if (duration < 0) throw new IOException("record_display_timing_unbound");
            timings.Add(new(receipt.Facts.AttemptId, start, terminal?.ObservedAtUtc, duration));
            previousConsumed = terminal?.ConsumedMilliseconds;
        }
        return new(runId, target, user, budget is null ? null : budget.OccurrenceId.HasValue ? "auto" : "manual",
            total.RetryPolicy.MaximumAttempts, budget?.FirstPreparedAtUtc, timings.AsReadOnly())
        {
            Steps = total.Selection.Select(item => new RecordStepDisplay(item.StepId,
                bundle.Scripts.Single(script => script.Id == item.ScriptId).Name)).ToArray(),
            Accounts = total.Selection.GroupBy(item => item.UserId).Select(group => new RecordAccountDisplay(
                Guid.Parse(group.Key), bundle.Users.Single(item => item.Id == group.Key).Name,
                group.Select(item => item.StepId).ToArray())).ToArray()
        };
    }
}
