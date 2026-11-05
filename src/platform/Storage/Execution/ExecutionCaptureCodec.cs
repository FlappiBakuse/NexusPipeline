using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class ExecutionCaptureCodec
{
    internal const string Format = "nexus.execution.capture";

    internal static bool IsParticipant(JsonElement participant)
    {
        if (!MatchesFormat(participant)) return false;
        _ = Decode(participant); return true;
    }

    private static bool MatchesFormat(JsonElement participant)
    {
        var identity = participant.GetProperty("identity");
        return identity.TryGetProperty("format", out var format) && format.GetString() == Format;
    }

    internal static ImmutableArray<ExecutionCaptureState> Read(JsonElement journal, ExecutionPlan total, string totalSha256)
    {
        journal = ExecutionInvocationCodec.Current(journal);
        var states = new Dictionary<string, ExecutionCaptureState>(StringComparer.Ordinal);
        var childStages = new Dictionary<string, int>(StringComparer.Ordinal);
        var priorRevision = 0L;
        var jobs = WriterIntentCodec.ReadJobs(journal, total);
        var ready = PcStageCodec.Read(journal, total, totalSha256);
        var android = AndroidStartupCodec.Read(journal, total, totalSha256);
        foreach (var participant in journal.GetProperty("participants").EnumerateArray().Where(MatchesFormat))
        {
            var item = Decode(participant); var binding = item.Binding; var primary = binding.Primary;
            if (total.CaptureRuntime is null || item.BeforeRevision <= priorRevision ||
                item.BeforeRevision >= journal.GetProperty("revision").GetInt64()) throw Invalid();
            priorRevision = item.BeforeRevision;
            var step = total.Steps.SingleOrDefault(step => step.StepId == primary.StepId) ?? throw Invalid();
            var job = jobs.SingleOrDefault(job => job.StepId == primary.StepId && job.Role == "process") ?? throw Invalid();
            if (StepResultCodec.Binding(journal, job) != primary || binding.TotalPlanSha256 != totalSha256 ||
                binding.CaptureId == Guid.Empty || !JobName(binding.HelperJobName) || binding.HelperJobName == primary.JobName ||
                !(step.Target is { Kind: "pc", Pc: not null, Android: null } &&
                    ready.Any(item => item.Binding.StepId == primary.StepId &&
                        (item.Outcome is TargetExecutionOutcome.OwnedReady or TargetExecutionOutcome.ExternalReady ||
                            !step.Target.LaunchByHost && item.Outcome == TargetExecutionOutcome.Skipped)) ||
                    step.Target is { Kind: "android", Pc: null, Android.Driver: "genericAdb" or "mumu" } &&
                    android.Any(item => item.Binding.StepId == primary.StepId &&
                        item.Outcome is TargetExecutionOutcome.ControlledReady or TargetExecutionOutcome.Skipped)) ||
                participant.GetProperty("kind").GetString() != "process" ||
                participant.GetProperty("resourceKey").GetString() != "automation:" + step.ScriptId) throw Invalid();
            StepEvidenceCodec.Plan(item.Artifact);
            if (item.Stage == "source")
            {
                if (states.ContainsKey(primary.StepId) || states.Values.Any(prior => prior.Binding.CaptureId == binding.CaptureId ||
                    prior.Binding.HelperJobName == binding.HelperJobName)) throw Invalid();
                states.Add(primary.StepId, new(binding, item.Artifact, null, null)); childStages.Add(primary.StepId, 0);
                continue;
            }
            if (!states.TryGetValue(primary.StepId, out var previous) || previous.Binding != binding || previous.Result is not null)
                throw Invalid();
            if (MuMuCaptureSequence.IsChild(item.Stage))
            {
                var progress = childStages[primary.StepId];
                if (previous.Root is null || !MuMuCaptureSequence.IsPaired(step) || progress >= 6 ||
                    item.Stage != MuMuCaptureSequence.Stage(progress / 2 + 1, progress % 2 != 0)) throw Invalid();
                childStages[primary.StepId] = progress + 1; continue;
            }
            states[primary.StepId] = item.Stage switch
            {
                "root" when previous.Root is null => previous with { Root = item.Artifact },
                "collected" => previous with { Result = item.Artifact },
                _ => throw Invalid()
            };
        }
        return total.Steps.Where(step => states.ContainsKey(step.StepId)).Select(step => states[step.StepId]).ToImmutableArray();
    }

    internal static void RequireResolved(JsonElement journal, ExecutionPlan total, string totalSha256, string? stepId = null)
    {
        if (Read(journal, total, totalSha256).Any(item => (stepId is null || item.Binding.Primary.StepId == stepId) && item.Result is null))
            throw new IOException("execution_capture_collection_pending");
    }

    internal static JsonObject Participant(ExecutionCaptureIntent item, string scriptId) => new()
    {
        ["participantId"] = Guid.NewGuid().ToString("D"), ["kind"] = "process",
        ["resourceKey"] = "automation:" + scriptId,
        ["identity"] = JsonSerializer.SerializeToNode(item, StepResultCodec.Options)
    };

    internal static ExecutionCaptureIntent Decode(JsonElement participant)
    {
        var identity = participant.GetProperty("identity");
        var decoded = ExecutionParticipantText.Read<ExecutionCaptureIntent>(identity, StepResultCodec.Options, Invalid);
        var value = decoded.Value;
        if (!decoded.MatchesOriginal ||
            value.Format != Format || value.Version != 1 || value.Binding?.Primary is null || value.Artifact is null ||
            value.Stage is not ("source" or "root" or "collected") && !MuMuCaptureSequence.IsChild(value.Stage)) throw Invalid();
        return value;
    }

    internal static bool JobName(string name) => name is { Length: 50 } && name.StartsWith("Local\\NexusWorker-", StringComparison.Ordinal) &&
        Guid.TryParseExact(name[18..], "N", out var id) && id != Guid.Empty && name[18..] == id.ToString("N");

    internal static IOException Invalid() => new("execution_capture_source_unbound");
}
