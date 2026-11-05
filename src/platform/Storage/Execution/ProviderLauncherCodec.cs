using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class ProviderLauncherCodec
{
    internal const string Format = "nexus.execution.provider-launcher";
    internal const int MaximumRetentionIntents = 3;

    internal static bool IsParticipant(JsonElement participant)
    {
        var identity = participant.GetProperty("identity");
        if (!identity.TryGetProperty("format", out var format) || format.GetString() != Format) return false;
        _ = Decode(participant); return true;
    }

    internal static ImmutableArray<ProviderLauncherIntent> ReadAll(JsonElement journal, ExecutionPlan total, string totalSha)
    {
        journal = ExecutionInvocationCodec.Current(journal);
        var result = ImmutableArray.CreateBuilder<ProviderLauncherIntent>();
        var states = new Dictionary<string, ProviderLauncherIntent>(StringComparer.Ordinal);
        foreach (var participant in journal.GetProperty("participants").EnumerateArray().Where(IsParticipant))
        {
            var item = Decode(participant); var binding = item.Binding;
            var step = total.Steps.SingleOrDefault(step => step.StepId == binding.StepId) ?? throw Invalid();
            if (binding.InstanceId.ToString("D") != total.InstanceId ||
                binding.ParentJournalId.ToString("D") != journal.GetProperty("journalId").GetString() ||
                binding.ParentOperationId.ToString("D") != journal.GetProperty("operationId").GetString() ||
                binding.AttemptId.ToString("D") != journal.GetProperty("attemptId").GetString() ||
                binding.TotalPlanSha256 != totalSha || step.EngineKind != "extensionWorker" ||
                step.Provider is not { Launcher: not null } provider || provider.ExtensionId != binding.ExtensionId ||
                provider.ActivationId != binding.ActivationId.ToString("D") || provider.EntryId != binding.EntryId ||
                provider.Launcher.WritesManagedConfiguration || provider.Launcher.TerminalProtocol != "worker-terminal/1" ||
                participant.GetProperty("resourceKey").GetString() != "automation:" + step.ScriptId) throw Invalid();
            if (item.Stage == "registered")
            {
                if (item.Artifact is not null || states.ContainsKey(binding.StepId) ||
                    states.Values.Any(prior => prior.Binding.JobName == binding.JobName || prior.Binding.LauncherId == binding.LauncherId)) throw Invalid();
            }
            else if (!states.TryGetValue(binding.StepId, out var before) || before.Binding != binding ||
                item.Artifact is null || !Transition(before.Stage, item.Stage)) throw Invalid();
            if (item.Stage == "retentionPrepared" &&
                result.Count(prior => prior.Binding == binding && prior.Stage == "retentionPrepared") >= MaximumRetentionIntents) throw Invalid();
            states[binding.StepId] = item; result.Add(item);
        }
        return result.ToImmutable();
    }

    internal static ImmutableArray<ProviderLauncherIntent> ReadHistory(JsonElement journal, ExecutionPlan total, string sha)
    {
        foreach (var view in ExecutionInvocationCodec.Views(journal)) _ = ReadAll(view, total, sha);
        return journal.GetProperty("participants").EnumerateArray().Where(IsParticipant).Select(Decode).ToImmutableArray();
    }

    internal static ImmutableArray<ProviderLauncherState> Read(JsonElement journal, ExecutionPlan total, string sha) =>
        ReadAll(journal, total, sha).GroupBy(item => item.Binding.StepId, StringComparer.Ordinal)
            .Select(group => group.Last()).Select(item => new ProviderLauncherState(item.Binding, item.Stage, item.Artifact, item.LaunchEngine)).ToImmutableArray();

    internal static JsonObject Participant(ProviderLauncherIntent value, string scriptId) => new()
    {
        ["participantId"] = Guid.NewGuid().ToString("D"), ["kind"] = "process", ["resourceKey"] = "automation:" + scriptId,
        ["identity"] = JsonSerializer.SerializeToNode(value, StepResultCodec.Options)
    };

    internal static void RequireResolved(JsonElement journal, ExecutionPlan total, string sha, string? stepId = null)
    {
        if (Read(journal, total, sha).Any(item => (stepId is null || item.Binding.StepId == stepId) &&
            item.Stage is not ("stopped" or "retained" or "recoveredStopped" or "recoveredRetained"))) throw new IOException("execution_provider_launcher_collection_pending");
    }

    private static ProviderLauncherIntent Decode(JsonElement participant)
    {
        var json = participant.GetProperty("identity");
        var value = json.Deserialize<ProviderLauncherIntent>(StepResultCodec.Options) ?? throw Invalid();
        var binding = value.Binding ?? throw Invalid();
        if (!JsonElement.DeepEquals(json, JsonSerializer.SerializeToElement(value, StepResultCodec.Options)) ||
            value.Format != Format || value.Version != 1 || participant.GetProperty("kind").GetString() != "process" ||
            binding.InstanceId == Guid.Empty || binding.ParentJournalId == Guid.Empty || binding.ParentOperationId == Guid.Empty ||
            binding.AttemptId == Guid.Empty || binding.LauncherId == Guid.Empty || binding.OwnerWriterId == Guid.Empty ||
            binding.ActivationId == Guid.Empty || binding.SessionId < 0 || string.IsNullOrWhiteSpace(binding.StepId) ||
            string.IsNullOrWhiteSpace(binding.ExtensionId) || string.IsNullOrWhiteSpace(binding.EntryId) ||
            binding.TotalPlanSha256 is not { Length: 64 } || binding.TotalPlanSha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
            binding.JobName is not { Length: 50 } || !binding.JobName.StartsWith("Local\\NexusWorker-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(binding.JobName[18..], "N", out var job) || job == Guid.Empty || binding.JobName[18..] != job.ToString("N") ||
            value.Stage is not ("registered" or "root" or "started" or "retentionPrepared" or "retained" or "stopped" or "recoveredStopped" or "recoveredRetained")) throw Invalid();
        if (value.Stage is "stopped" or "recoveredStopped" ? value.LaunchEngine is not (EngineOutcome.NotStarted or EngineOutcome.Succeeded or EngineOutcome.Failed or EngineOutcome.Unknown) :
            value.LaunchEngine is not null) throw Invalid();
        if (value.Artifact is not null) StepEvidenceCodec.Plan(value.Artifact);
        return value;
    }

    private static bool Transition(string before, string after) => (before, after) switch
    {
        ("registered", "root") or ("root", "started") or ("started", "retentionPrepared") or ("retentionPrepared", "retentionPrepared") or
        ("retentionPrepared", "retained") or ("registered" or "root" or "started" or "retentionPrepared", "stopped") or
        ("registered" or "root" or "started" or "retentionPrepared", "recoveredStopped") or ("retentionPrepared", "recoveredRetained") => true,
        _ => false
    };

    private static IOException Invalid() => new("execution_provider_launcher_unrecognized");
}
