using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class TargetJobCodec
{
    internal const string Format = "nexus.execution.target-job";
    internal const int MaximumRetentionIntents = 3;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    internal static bool IsParticipant(JsonElement participant)
    {
        var identity = participant.GetProperty("identity");
        return identity.TryGetProperty("format", out var format) && format.GetString() == Format;
    }

    internal static ImmutableArray<TargetJobState> Read(JsonElement journal, ExecutionPlan total, string totalPlanSha256)
        => ReadCore(journal, total, totalPlanSha256, null);

    private static ImmutableArray<TargetJobState> ReadCore(JsonElement journal, ExecutionPlan total, string totalPlanSha256,
        ImmutableArray<TargetJobIntent>.Builder? intents)
    {
        journal = ExecutionInvocationCodec.Current(journal);
        var states = new Dictionary<string, TargetJobState>(StringComparer.Ordinal);
        var retentionCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var participant in journal.GetProperty("participants").EnumerateArray().Where(IsParticipant))
        {
            var identity = participant.GetProperty("identity");
            var decoded = ExecutionParticipantText.Read<TargetJobIntent>(identity, Options, Invalid);
            var item = decoded.Value;
            intents?.Add(item);
            var binding = item.Binding ?? throw Invalid();
            if (!decoded.MatchesOriginal || item.Format != Format || item.Version != 1 ||
                binding.InstanceId.ToString("D") != total.InstanceId || binding.ParentJournalId.ToString("D") != journal.GetProperty("journalId").GetString() ||
                binding.ParentOperationId.ToString("D") != journal.GetProperty("operationId").GetString() ||
                binding.AttemptId.ToString("D") != journal.GetProperty("attemptId").GetString() || binding.TargetId == Guid.Empty ||
                binding.OwnerWriterId == Guid.Empty || binding.SessionId < 0 ||
                binding.TotalPlanSha256 != totalPlanSha256 ||
                binding.JobName is null || binding.JobName.Length != 50 || !binding.JobName.StartsWith("Local\\NexusWorker-", StringComparison.Ordinal) ||
                !Guid.TryParseExact(binding.JobName[18..], "N", out var jobId) || jobId == Guid.Empty || binding.JobName[18..] != jobId.ToString("N") ||
                participant.GetProperty("kind").GetString() != "process") throw Invalid();
            var step = total.Steps.SingleOrDefault(step => step.StepId == binding.StepId) ?? throw Invalid();
            if (step.Target is not { Kind: "pc", LaunchByHost: true, Pc: not null } ||
                participant.GetProperty("resourceKey").GetString() != "automation:" + step.ScriptId) throw Invalid();
            if (item.Stage == "registered")
            {
                if (item.Evidence is not null || states.ContainsKey(binding.StepId) ||
                    states.Values.Any(value => value.Binding.TargetId == binding.TargetId || value.Binding.JobName == binding.JobName)) throw Invalid();
                states.Add(binding.StepId, new(binding, item.Stage, null));
                continue;
            }
            if (!states.TryGetValue(binding.StepId, out var previous) || previous.Binding != binding || item.Evidence is null ||
                !Transition(previous.Stage, item.Stage)) throw Invalid();
            if (item.Stage == "retentionPrepared")
            {
                var count = retentionCounts.GetValueOrDefault(binding.StepId) + 1;
                if (count > MaximumRetentionIntents) throw Invalid();
                retentionCounts[binding.StepId] = count;
            }
            StepEvidenceCodec.Plan(item.Evidence);
            states[binding.StepId] = new(binding, item.Stage, item.Evidence);
        }
        return total.Steps.Where(step => states.ContainsKey(step.StepId)).Select(step => states[step.StepId]).ToImmutableArray();
    }

    internal static JsonObject Participant(TargetJobIntent item, string scriptId) => new()
    {
        ["participantId"] = Guid.NewGuid().ToString("D"), ["kind"] = "process", ["resourceKey"] = "automation:" + scriptId,
        ["identity"] = JsonSerializer.SerializeToNode(item, Options)
    };

    internal static ImmutableArray<TargetJobIntent> ReadAll(JsonElement journal, ExecutionPlan total, string totalPlanSha256)
    {
        var intents = ImmutableArray.CreateBuilder<TargetJobIntent>();
        _ = ReadCore(journal, total, totalPlanSha256, intents);
        return intents.ToImmutable();
    }

    internal static ImmutableArray<TargetJobIntent> ReadHistory(JsonElement journal, ExecutionPlan total, string sha)
    {
        foreach (var view in ExecutionInvocationCodec.Views(journal)) _ = Read(view, total, sha);
        return journal.GetProperty("participants").EnumerateArray().Where(IsParticipant)
            .Select(item => item.GetProperty("identity").Deserialize<TargetJobIntent>(Options) ?? throw Invalid()).ToImmutableArray();
    }

    private static bool Transition(string before, string after) => (before, after) switch
    {
        ("registered", "root") or ("root", "ready") or
        ("root" or "ready", "retentionPrepared") or ("retentionPrepared", "retentionPrepared") or
        ("retentionPrepared", "retained") => true,
        ("registered" or "root" or "ready" or "retentionPrepared", "stopped") => true,
        ("registered" or "root" or "ready" or "retentionPrepared", "recoveredStopped") or ("retentionPrepared", "recoveredRetained") => true,
        _ => false
    };

    private static JsonException Invalid() => new("execution_target_job_unrecognized");
}
