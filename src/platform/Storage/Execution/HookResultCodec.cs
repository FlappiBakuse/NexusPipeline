using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Execution;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record HookResultIntent(string Format, int Version, string StepId, string Role,
    StepEvidenceBinding Binding, BlobReference Artifact, EngineOutcome Engine, string ReasonCode);

internal static class HookResultCodec
{
    internal const string Format = "nexus.execution.hook-result";

    internal static bool IsParticipant(JsonElement participant)
    {
        var identity = participant.GetProperty("identity");
        if (!identity.TryGetProperty("format", out var format) || format.GetString() != Format) return false;
        _ = Decode(participant); return true;
    }

    internal static ImmutableArray<HookResultIntent> Read(JsonElement journal, NexusPipeline.Platform.Serialization.Protocol.ExecutionPlan total)
    {
        journal = ExecutionInvocationCodec.Current(journal);
        var jobs = WriterIntentCodec.ReadJobs(journal, total);
        var result = ImmutableArray.CreateBuilder<HookResultIntent>();
        foreach (var participant in journal.GetProperty("participants").EnumerateArray().Where(IsParticipant))
        {
            var item = Decode(participant);
            var step = total.Steps.SingleOrDefault(step => step.StepId == item.StepId) ?? throw Invalid();
            var job = jobs.SingleOrDefault(job => job.StepId == item.StepId && job.Role == item.Role) ?? throw Invalid();
            if (item.Binding != StepResultCodec.Binding(journal, job) || job.ClosedElapsedMilliseconds is null ||
                result.Any(prior => prior.StepId == item.StepId && prior.Role == item.Role) ||
                participant.GetProperty("resourceKey").GetString() != "automation:" + step.ScriptId) throw Invalid();
            _ = ExecutionBindingRoles.Hook(step, item.Role);
            result.Add(item);
        }
        return result.ToImmutable();
    }

    internal static ImmutableArray<HookResultIntent> ReadHistory(JsonElement journal, NexusPipeline.Platform.Serialization.Protocol.ExecutionPlan total) =>
        ExecutionInvocationCodec.Views(journal).SelectMany(view => Read(view, total)).DistinctBy(item => item.Binding.JobName).ToImmutableArray();

    internal static JsonObject Participant(HookResultIntent item, string scriptId) => new()
    {
        ["participantId"] = Guid.NewGuid().ToString("D"), ["kind"] = "database", ["resourceKey"] = "automation:" + scriptId,
        ["identity"] = JsonSerializer.SerializeToNode(item, StepResultCodec.Options)
    };

    private static HookResultIntent Decode(JsonElement participant)
    {
        var identity = participant.GetProperty("identity");
        var item = identity.Deserialize<HookResultIntent>(StepResultCodec.Options) ?? throw Invalid();
        if (!JsonElement.DeepEquals(identity, JsonSerializer.SerializeToElement(item, StepResultCodec.Options)) ||
            item.Format != Format || item.Version != 1 || string.IsNullOrWhiteSpace(item.StepId) || item.StepId.Length > 120 ||
            !ExecutionBindingRoles.IsHook(item.Role) || item.Binding is null || item.Binding.StepId != item.StepId ||
            !Enum.IsDefined(item.Engine) || item.Engine == EngineOutcome.Running ||
            item.ReasonCode is not ("execution_hook_succeeded" or "execution_hook_failed" or "execution_hook_not_started" or "execution_hook_output_unverified" or "host_interrupted") ||
            participant.GetProperty("kind").GetString() != "database") throw Invalid();
        StepEvidenceCodec.Plan(item.Artifact);
        return item;
    }

    private static JsonException Invalid() => new("execution_hook_result_unrecognized");
}
