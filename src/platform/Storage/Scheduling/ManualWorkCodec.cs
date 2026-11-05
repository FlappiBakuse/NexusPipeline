using NexusPipeline.Core.Scheduling;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Scheduling;

internal sealed class ManualWorkCodec(RuntimeInstance instance, ContractReader contracts)
{
    internal T ReadDocument<T>(BlobReference reference) => new ScheduledProviderCodec(instance, contracts).ReadDocument<T>(reference);
    internal BlobReference Persist<T>(T value) => new ImmutableBlobStore(instance, contracts).PersistPlan(ScheduledProviderCodec.Encode(value));

    internal ManualWorkSnapshot Read(BlobReference reference)
    {
        var source = ReadDocument<ManualWorkDocument>(reference); Require(source); return new(source, reference);
    }

    internal void Require(ManualWorkDocument source)
    {
        if (source.Format != "nexus.manual.work-source" || source.Version != 1 || source.InstanceId != instance.InstanceId ||
            source.Task is null || source.Invocation is null || source.Creator is null || source.ActivationId == Guid.Empty ||
            source.OwnerGeneration == Guid.Empty || source.ClientId == Guid.Empty || source.CallerSubject is not { Length: >= 1 and <= 256 } ||
            string.IsNullOrWhiteSpace(source.CallerSubject) || source.CallerSubject.Contains('\0') || source.InitialConsumedMilliseconds < 0 ||
            source.InitialConsumedMilliseconds >= source.Task.TimeoutSeconds * 1000L) throw Invalid();
        if (source.ObservedAtUtc < source.StartedAtUtc || source.ObservedAtUtc >= source.Invocation.DeadlineUtc) throw Invalid();
        WorkCalendar.Require(source.ObservedAtUtc, source.LocalTimeZoneId, source.LocalDate);
        var expected = ManualRunPolicy.Freeze(instance.InstanceId, source.OperationId, source.ExtensionId, source.ProviderId,
            source.Task, source.StartedAtUtc);
        if (!expected.Matches(source.Invocation)) throw Invalid();
        JournalWriterPolicy.Require(source.Creator);
    }

    internal static OperationIdentity Identity(ManualWorkSnapshot source) => new(source.Document.OperationId, source.Document.CallerSubject,
        source.Reference.Sha256, ManualWorkRows.Kind);

    internal static void Require(ManualWorkState state)
    {
        if ((state.BeginReference is null) != (state.Begin is null) || (state.ResultReference is null) != (state.Result is null)) throw Invalid();
        var source = state.Source;
        var limit = source.Document.Task.TimeoutSeconds * 1000L;
        if (state.Begin is { } begin && (begin.Format != "nexus.manual.work-begin" || begin.Version != 1 || begin.Source != source.Reference ||
            begin.ObservedAtUtc.Offset != TimeSpan.Zero || begin.ObservedAtUtc < source.Document.ObservedAtUtc ||
            begin.ObservedAtUtc >= source.Document.Invocation.DeadlineUtc || begin.ConsumedMilliseconds < source.Document.InitialConsumedMilliseconds ||
            begin.ConsumedMilliseconds >= limit)) throw Invalid();
        if (state.Result is not { } result) return;
        var regressed = result.ObservedAtUtc < source.Document.ObservedAtUtc || state.Begin is { } started && result.ObservedAtUtc < started.ObservedAtUtc;
        if (result.Format != "nexus.manual.work-result" || result.Version != 1 || result.Source != source.Reference ||
            result.Begin != state.BeginReference || result.ObservedAtUtc.Offset != TimeSpan.Zero || result.WallClockRegressed != regressed ||
            !Reason(result.ReasonCode) || result.Completion is not ("notStarted" or "returned" or "unknown") ||
            (result.Completion == "notStarted") != (state.Begin is null) ||
            (result.Completion == "returned") != (result.Returned is not null) ||
            result.Returned is { } returned && (!Enum.IsDefined(returned.Business) || !Reason(returned.ReasonCode)) ||
            result.FinalConsumedMilliseconds is { } consumed && (consumed < (state.Begin?.ConsumedMilliseconds ?? source.Document.InitialConsumedMilliseconds) || consumed > limit) ||
            result.Completion == "returned" && (result.FinalConsumedMilliseconds is null || regressed)) throw Invalid();
    }

    private static bool Reason(string value) => value is { Length: >= 1 and <= 80 } && value[0] is >= 'a' and <= 'z' &&
        value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');
    internal static IOException Invalid() => new("manual_work_evidence_unbound");
}
