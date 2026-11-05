using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record ProviderTerminalIntent(string Format, int Version, StepEvidenceBinding Binding,
    Guid ActivationId, string EntryId, long BeforeRevision, BlobReference Artifact, ExecutionProviderDecision Decision);

internal static class ProviderTerminalCodec
{
    internal const string Format = "nexus.execution.provider-terminal";

    internal static bool IsParticipant(JsonElement participant)
    {
        var identity = participant.GetProperty("identity");
        if (!identity.TryGetProperty("format", out var format) || format.GetString() != Format) return false;
        _ = Decode(participant); return true;
    }

    internal static ImmutableArray<ProviderTerminalIntent> Read(JsonElement journal, ExecutionPlan total)
    {
        journal = ExecutionInvocationCodec.Current(journal);
        var jobs = WriterIntentCodec.Read(journal, total).ToDictionary(item => item.StepId);
        var result = ImmutableArray.CreateBuilder<ProviderTerminalIntent>();
        var closed = false;
        foreach (var participant in journal.GetProperty("participants").EnumerateArray())
        {
            if (DispatchIntentCodec.IsParticipant(participant)) closed = true;
            if (!IsParticipant(participant)) continue;
            var item = Decode(participant);
            var step = total.Steps.SingleOrDefault(step => step.StepId == item.Binding.StepId) ?? throw Invalid();
            if (closed || !jobs.TryGetValue(step.StepId, out var job) ||
                StepResultCodec.Binding(journal, job) != item.Binding ||
                step.EngineKind != "extensionWorker" || step.Provider is null ||
                Guid.Parse(step.Provider.ActivationId) != item.ActivationId || step.Provider.EntryId != item.EntryId ||
                participant.GetProperty("resourceKey").GetString() != "automation:" + step.ScriptId ||
                item.BeforeRevision >= journal.GetProperty("revision").GetInt64() ||
                result.Any(prior => prior.Binding.StepId == step.StepId)) throw Invalid();
            result.Add(item);
        }
        return result.ToImmutable();
    }

    internal static ImmutableArray<ProviderTerminalIntent> ReadHistory(JsonElement journal, ExecutionPlan total)
    {
        foreach (var view in ExecutionInvocationCodec.Views(journal)) _ = Read(view, total);
        return journal.GetProperty("participants").EnumerateArray().Where(IsParticipant).Select(Decode).ToImmutableArray();
    }

    internal static JsonObject Participant(ProviderTerminalIntent value, string scriptId) => new()
    {
        ["participantId"] = Guid.NewGuid().ToString("D"), ["kind"] = "process", ["resourceKey"] = "automation:" + scriptId,
        ["identity"] = JsonSerializer.SerializeToNode(value, StepResultCodec.Options)
    };

    private static ProviderTerminalIntent Decode(JsonElement participant)
    {
        var json = participant.GetProperty("identity");
        var value = json.Deserialize<ProviderTerminalIntent>(StepResultCodec.Options) ?? throw Invalid();
        if (!JsonElement.DeepEquals(json, JsonSerializer.SerializeToElement(value, StepResultCodec.Options)) ||
            value.Format != Format || value.Version != 1 || value.Binding is null || value.ActivationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(value.EntryId) || value.BeforeRevision < 0 || value.Decision is null ||
            value.Decision.Sequence is < 2 or > ProviderProtocolState.MaximumEvents ||
            value.Decision.Engine is not (EngineOutcome.Succeeded or EngineOutcome.Failed or EngineOutcome.Cancelled) ||
            value.Decision.ReasonCode is not { Length: >= 1 and <= 80 } ||
            value.Decision.ReasonCode[0] is not (>= 'a' and <= 'z') ||
            value.Decision.ReasonCode.Any(character => character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_')) ||
            participant.GetProperty("kind").GetString() != "process") throw Invalid();
        StepEvidenceCodec.Plan(value.Artifact);
        return value;
    }

    private static IOException Invalid() => new("execution_provider_terminal_unrecognized");
}
