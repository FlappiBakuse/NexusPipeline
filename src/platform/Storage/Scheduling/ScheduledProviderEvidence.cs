using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Scheduling;

internal sealed record ScheduledProviderState(ScheduledProviderSnapshot Source, BlobReference? BeginReference,
    ScheduledProviderBegin? Begin, BlobReference? CloseReference, ScheduledProviderClose? Close,
    BlobReference? ResultReference, ScheduledProviderResult? Result);

internal sealed class ScheduledProviderEvidence(RuntimeInstance instance, ContractReader contracts)
{
    internal ScheduledProviderState Read(BlobReference reference, SqliteConnection connection, SqliteTransaction transaction)
    {
        var codec = new ScheduledProviderCodec(instance, contracts); var source = codec.Read(reference);
        ScheduledProviderRows.RequireSaved(connection, transaction, source);
        var id = source.Document.Invocation.OccurrenceId;
        var beginRef = ScheduledProviderRows.Leaf(connection, transaction, id, "begin");
        var closeRef = ScheduledProviderRows.Leaf(connection, transaction, id, "close");
        var resultRef = ScheduledProviderRows.Leaf(connection, transaction, id, "result");
        var begin = beginRef is null ? null : codec.ReadDocument<ScheduledProviderBegin>(beginRef);
        var close = closeRef is null ? null : codec.ReadDocument<ScheduledProviderClose>(closeRef);
        var result = resultRef is null ? null : codec.ReadDocument<ScheduledProviderResult>(resultRef);
        Require(source, beginRef, begin, close, result);
        var operation = OperationRows.ReadExisting(connection, transaction, ScheduledProviderRows.Identity(source)) ??
            throw ScheduledProviderCodec.Invalid();
        if (operation.Status != (result is null ? "accepted" : "completed")) throw ScheduledProviderCodec.Invalid();
        using var pending = DatabaseConnections.Command(connection, transaction,
            "SELECT trigger_utc_minute FROM main.scheduled_provider_pending WHERE occurrence_id=$0;", id.ToString("D"));
        var indexed = pending.ExecuteScalar();
        if (result is null ? indexed is not long minute || minute != source.Document.TriggerUtcMinute : indexed is not null)
            throw ScheduledProviderCodec.Invalid();
        return new(source, beginRef, begin, closeRef, close, resultRef, result);
    }

    internal static void Require(ScheduledProviderSnapshot source, BlobReference? beginRef, ScheduledProviderBegin? begin,
        ScheduledProviderClose? close, ScheduledProviderResult? result)
    {
        var document = source.Document; var maximum = document.Task.TimeoutSeconds * 1000L;
        if (begin is not null && (begin.Format != "nexus.schedule.provider-begin" || begin.Version != 1 ||
            begin.Source != source.Reference || begin.ObservedAtUtc < document.ObservedAtUtc || begin.ObservedAtUtc.Offset != TimeSpan.Zero ||
            begin.ConsumedMilliseconds < document.InitialConsumedMilliseconds || begin.ConsumedMilliseconds > maximum))
            throw ScheduledProviderCodec.Invalid();
        if (close is not null && (close.Format != "nexus.schedule.provider-close" || close.Version != 1 || close.Source != source.Reference ||
            close.ReasonCode is not ("scheduled_provider_cancelled" or "scheduled_provider_deadline" or "scheduled_provider_revoked" or
                "scheduled_provider_owner_lost" or "scheduled_provider_clock_regressed"))) throw ScheduledProviderCodec.Invalid();
        if (result is null) return;
        if (result.Format != "nexus.schedule.provider-result" || result.Version != 1 || result.Source != source.Reference ||
            result.Begin != beginRef || result.ObservedAtUtc.Offset != TimeSpan.Zero ||
            result.WallClockRegressed != (result.ObservedAtUtc < document.ObservedAtUtc || begin is not null && result.ObservedAtUtc < begin.ObservedAtUtc) ||
            result.Completion is not ("returned" or "notStarted" or "unknown") ||
            result.FinalConsumedMilliseconds is { } consumed && (consumed < (begin?.ConsumedMilliseconds ?? document.InitialConsumedMilliseconds) || consumed > maximum))
            throw ScheduledProviderCodec.Invalid();
        switch (result.Completion)
        {
            case "returned":
                if (begin is null || close is not null || result.Returned is not { } returned ||
                    result.FinalConsumedMilliseconds is null || !Enum.IsDefined(returned.Business) ||
                    !Reason(returned.ReasonCode) || result.ReasonCode != returned.ReasonCode) throw ScheduledProviderCodec.Invalid();
                break;
            case "notStarted":
                if (begin is not null || result.Returned is not null || close is null || result.ReasonCode != close.ReasonCode)
                    throw ScheduledProviderCodec.Invalid();
                break;
            case "unknown":
                if (result.Returned is not null || result.ReasonCode is not ("scheduled_provider_callback_unverified" or
                    "scheduled_provider_owner_lost" or "scheduled_provider_cancelled" or "scheduled_provider_deadline" or "scheduled_provider_revoked" or
                    "scheduled_provider_clock_regressed"))
                    throw ScheduledProviderCodec.Invalid();
                break;
        }
    }

    internal ImmutableArray<BlobReference> References(ScheduledProviderState state) =>
        new BlobReference?[] { state.Source.Reference, state.Source.Document.Plans, state.BeginReference,
            state.CloseReference, state.ResultReference }.OfType<BlobReference>().Distinct().ToImmutableArray();

    private static bool Reason(string value) => !string.IsNullOrEmpty(value) && value.Length <= 80 &&
        value[0] is >= 'a' and <= 'z' && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');
}
