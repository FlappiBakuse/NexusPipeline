using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record StepWriterIntent(string Format, int Version, string StepId, string Stage,
    string JobName, Guid WriterId, int SessionId, long? ElapsedMilliseconds)
{
    public string Role { get; init; } = "process";
    public Guid? JudgementOwnerId { get; init; }
    public BlobReference? FileLogBaseline { get; init; }
}

internal sealed record StepWriterState(string StepId, string JobName, Guid WriterId, int SessionId,
    long? ClosedElapsedMilliseconds)
{
    public string Role { get; init; } = "process";
    public Guid? JudgementOwnerId { get; init; }
    public BlobReference? FileLogBaseline { get; init; }
}

internal static class WriterIntentCodec
{
    internal const string Format = "nexus.execution.step-writer";
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static bool IsParticipant(JsonElement participant)
    {
        if (!MatchesFormat(participant)) return false;
        _ = Decode(participant);
        return true;
    }

    private static bool MatchesFormat(JsonElement participant)
    {
        var identity = participant.GetProperty("identity");
        return identity.TryGetProperty("format", out var format) && format.GetString() == Format;
    }

    internal static ImmutableArray<StepWriterState> Read(JsonElement journal, ExecutionPlan total)
        => ReadJobs(journal, total).Where(item => item.Role == "process").ToImmutableArray();

    internal static ImmutableArray<StepWriterState> ReadJobs(JsonElement journal, ExecutionPlan total)
    {
        if (AttemptReadScope.TryRead<StepWriterState>(journal, total, out var saved)) return saved;
        var original = journal;
        journal = ExecutionInvocationCodec.Current(journal);
        var skippedSteps = BindingSkipCodec.Read(journal, total).Select(value => value.StepId).ToHashSet(StringComparer.Ordinal);
        var states = new Dictionary<(string Step, string Role), StepWriterState>();
        var dispatched = new List<string>();
        var lastRole = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var participant in journal.GetProperty("participants").EnumerateArray())
        {
            if (BindingSkipCodec.Matches(participant))
            {
                var skipped = BindingSkipCodec.Decode(participant, total);
                if (dispatched.Count >= total.Steps.Count || total.Steps[dispatched.Count].StepId != skipped.StepId) throw Invalid();
                dispatched.Add(skipped.StepId); continue;
            }
            if (!MatchesFormat(participant)) continue;
            var item = Decode(participant);
            var step = total.Steps.SingleOrDefault(step => step.StepId == item.StepId) ?? throw Invalid();
            if (item.Role == "process" && (step.LogTargets.Count != 0) != (item.FileLogBaseline is not null) ||
                item.Role != "process" && item.FileLogBaseline is not null) throw Invalid();
            if (participant.GetProperty("resourceKey").GetString() != "automation:" + step.ScriptId) throw Invalid();
            if (item.Stage == "registered")
            {
                if (skippedSteps.Contains(item.StepId) ||
                    states.ContainsKey((item.StepId, item.Role)) || states.Values.Any(prior => prior.JobName == item.JobName)) throw Invalid();
                var required = ExecutionBindingRoles.Required(step);
                var roleIndex = required.IndexOf(item.Role);
                if (roleIndex < 0) throw Invalid();
                if (dispatched.LastOrDefault() != item.StepId)
                {
                    if (dispatched.Count >= total.Steps.Count || total.Steps[dispatched.Count].StepId != item.StepId) throw Invalid();
                    dispatched.Add(item.StepId);
                }
                if (lastRole.TryGetValue(item.StepId, out var priorRole) && roleIndex <= priorRole) throw Invalid();
                if (item.Role == "judgement" && !states.ContainsKey((item.StepId, "process"))) throw Invalid();
                lastRole[item.StepId] = roleIndex;
                states.Add((item.StepId, item.Role), new(item.StepId, item.JobName, item.WriterId, item.SessionId, null)
                    { Role = item.Role, JudgementOwnerId = item.JudgementOwnerId, FileLogBaseline = item.FileLogBaseline });
            }
            else
            {
                if (!states.TryGetValue((item.StepId, item.Role), out var prior) || prior.ClosedElapsedMilliseconds is not null ||
                    prior.JobName != item.JobName || prior.WriterId != item.WriterId || prior.SessionId != item.SessionId ||
                    prior.JudgementOwnerId != item.JudgementOwnerId || prior.FileLogBaseline != item.FileLogBaseline) throw Invalid();
                states[(item.StepId, item.Role)] = prior with { ClosedElapsedMilliseconds = item.ElapsedMilliseconds };
            }
        }
        return AttemptReadScope.Remember(original, total,
            total.Steps.SelectMany(step => ExecutionBindingRoles.Required(step).Where(role => states.ContainsKey((step.StepId, role)))
                .Select(role => states[(step.StepId, role)])).ToImmutableArray());
    }

    internal static ImmutableArray<StepWriterState> ReadAllJobs(JsonElement journal, ExecutionPlan total)
    {
        var jobs = new Dictionary<string, (int Ordinal, StepWriterState Job)>(StringComparer.Ordinal);
        foreach (var view in ExecutionInvocationCodec.Views(journal))
        foreach (var job in ReadJobs(view, total))
        {
            var ordinal = ExecutionInvocationCodec.Ordinal(view, job.StepId);
            if (jobs.TryGetValue(job.JobName, out var old) && (old.Ordinal != ordinal || old.Job.StepId != job.StepId || old.Job.Role != job.Role)) throw Invalid();
            jobs[job.JobName] = (ordinal, job);
        }
        return jobs.Values.Select(item => item.Job).ToImmutableArray();
    }

    internal static JsonObject Participant(StepWriterIntent item, string scriptId) => new()
    {
        ["participantId"] = Guid.NewGuid().ToString("D"), ["kind"] = "process", ["resourceKey"] = "automation:" + scriptId,
        ["identity"] = JsonSerializer.SerializeToNode(item, Options)
    };

    private static StepWriterIntent Decode(JsonElement participant)
    {
        var identity = participant.GetProperty("identity");
        var decoded = ExecutionParticipantText.Read<StepWriterIntent>(identity, Options, Invalid);
        var item = decoded.Value;
        if (item.FileLogBaseline is { } baseline) StepEvidenceCodec.Plan(baseline);
        if (!decoded.MatchesOriginal || item.Format != Format || item.Version != 1 ||
            string.IsNullOrWhiteSpace(item.StepId) || item.StepId.Length > 120 || item.WriterId == Guid.Empty || item.SessionId < 0 ||
            item.Role is not ("preHook" or "process" or "judgement" or "postHook") || (item.Role == "judgement") != item.JudgementOwnerId.HasValue || item.JudgementOwnerId == Guid.Empty ||
            item.JobName is null || item.JobName.Length != 50 || !item.JobName.StartsWith("Local\\NexusWorker-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(item.JobName[18..], "N", out var job) || job == Guid.Empty || item.JobName[18..] != job.ToString("N") ||
            participant.GetProperty("kind").GetString() != "process" ||
            item.Stage is not ("registered" or "closed") || (item.Stage == "registered") != (item.ElapsedMilliseconds is null) ||
            item.ElapsedMilliseconds is < 0 or > 922337203685477) throw Invalid();
        return item;
    }

    private static JsonException Invalid() => new("execution_step_writer_intent_unrecognized");
}
