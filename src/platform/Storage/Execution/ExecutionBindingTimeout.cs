using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record ExecutionBindingTimeout(string Format, int Version, string StepId, int InvocationOrdinal,
    long BeforeRevision, long ConsumedMilliseconds);

internal static class BindingTimeoutCodec
{
    internal const string Format = "nexus.execution.binding-timeout";
    internal const string Reason = "execution_binding_total_timeout";

    internal static bool IsParticipant(JsonElement participant)
    {
        var identity = participant.GetProperty("identity");
        if (!identity.TryGetProperty("format", out var format) || format.GetString() != Format) return false;
        _ = Decode(participant); return true;
    }

    internal static ImmutableArray<ExecutionBindingTimeout> Read(JsonElement journal, ExecutionPlan total)
    {
        journal = ExecutionInvocationCodec.Current(journal);
        var result = ImmutableArray.CreateBuilder<ExecutionBindingTimeout>();
        foreach (var participant in journal.GetProperty("participants").EnumerateArray().Where(IsParticipant))
        {
            var item = Decode(participant);
            var step = total.Steps.SingleOrDefault(step => step.StepId == item.StepId) ?? throw Invalid();
            var jobs = WriterIntentCodec.ReadJobs(journal, total).Where(job => job.StepId == item.StepId).ToArray();
            if (step.TotalTimeoutSeconds is not > 0 || item.ConsumedMilliseconds < step.TotalTimeoutSeconds * 1000 ||
                item.BeforeRevision >= journal.GetProperty("revision").GetInt64() ||
                item.InvocationOrdinal != ExecutionInvocationCodec.Ordinal(journal, item.StepId) || jobs.Length == 0 ||
                jobs.Any(job => job.ClosedElapsedMilliseconds is null) || result.Any(prior => prior.StepId == item.StepId) ||
                participant.GetProperty("resourceKey").GetString() != "automation:" + step.ScriptId) throw Invalid();
            result.Add(item);
        }
        return result.ToImmutable();
    }

    internal static JsonObject Participant(ExecutionBindingTimeout item, string scriptId) => new()
    {
        ["participantId"] = Guid.NewGuid().ToString("D"), ["kind"] = "database", ["resourceKey"] = "automation:" + scriptId,
        ["identity"] = JsonSerializer.SerializeToNode(item, StepResultCodec.Options)
    };

    private static ExecutionBindingTimeout Decode(JsonElement participant)
    {
        var identity = participant.GetProperty("identity");
        var item = identity.Deserialize<ExecutionBindingTimeout>(StepResultCodec.Options) ?? throw Invalid();
        if (!JsonElement.DeepEquals(identity, JsonSerializer.SerializeToElement(item, StepResultCodec.Options)) ||
            item.Format != Format || item.Version != 1 || string.IsNullOrWhiteSpace(item.StepId) || item.StepId.Length > 120 ||
            item.InvocationOrdinal is < 1 or > 99 || item.BeforeRevision is < 1 or > 4095 ||
            item.ConsumedMilliseconds is < 1 or > 9007199254740991 || participant.GetProperty("kind").GetString() != "database") throw Invalid();
        return item;
    }

    internal static IOException Invalid() => new("execution_binding_timeout_unbound");
}

internal sealed class BindingTimeoutStore(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease)
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal void Record(Guid parentJournalId, string stepId, TimeSpan consumed) => lease.PublishExecution(() =>
    {
        lease.VerifyHeld(instance);
        var state = AttemptWriterState.Read(instance, database, contracts, parentJournalId);
        if (BindingTimeoutCodec.Read(state.Latest.Root, state.Total).Any(item => item.StepId == stepId)) return;
        if (state.Gate.State != EffectGateState.Entered || state.Latest.Phase != JournalPhase.Applying ||
            state.Latest.Revision != state.Journal.Revision) throw BindingTimeoutCodec.Invalid();
        StepWritersEvidence.RequireStopped(instance, database, contracts, parentJournalId, state.Jobs.Where(job => job.StepId == stepId));
        AttemptTerminalEvidence.RequireArtifacts(instance, database, contracts, state);
        var item = new ExecutionBindingTimeout(BindingTimeoutCodec.Format, 1, stepId,
            ExecutionInvocationCodec.Ordinal(state.Latest.Root, stepId), state.Latest.Revision, consumed.Ticks / TimeSpan.TicksPerMillisecond);
        var document = JsonNode.Parse(state.Latest.Export())!.AsObject();
        document["revision"] = state.Latest.Revision + 1;
        document["updatedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");
        document["participants"]!.AsArray().Add(BindingTimeoutCodec.Participant(item, state.Total.Steps.Single(step => step.StepId == stepId).ScriptId));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document);
        _ = BindingTimeoutCodec.Read(JournalDocument.Read(bytes, contracts).Root, state.Total);
        new JournalStore(instance, contracts).Append(parentJournalId, state.Latest.Revision, bytes);
        ExecutionAttemptProjection.Synchronize(instance, database, contracts, AttemptWriterState.Read(instance, database, contracts, parentJournalId));
    });
}

internal sealed class BudgetExpiredException : Exception { }
