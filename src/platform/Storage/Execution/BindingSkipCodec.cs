using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record ExecutionBindingSkip(string Format, int Version, string StepId, string ReasonCode,
    DateTimeOffset EvaluatedAtUtc, long SuccessfulRunsToday)
{
    public DateTimeOffset DailyWindowStartUtc { get; init; }
    public DateTimeOffset DailyWindowEndUtc { get; init; }
}

internal static class BindingSkipCodec
{
    internal const string Format = "nexus.execution.binding-skip";
    internal static bool Matches(JsonElement participant) => participant.GetProperty("identity")
        .TryGetProperty("format", out var format) && format.GetString() == Format;

    internal static ExecutionBindingSkip Decode(JsonElement participant, NexusPipeline.Platform.Serialization.Protocol.ExecutionPlan total)
    {
        var value = ReadShape(participant);
        var step = total.Steps.SingleOrDefault(step => step.StepId == value.StepId) ?? throw Invalid();
        if (value.ReasonCode == "execution_daily_success_limit" && (step.MaximumSuccessfulRunsPerDay is not > 0 ||
                value.SuccessfulRunsToday < step.MaximumSuccessfulRunsPerDay || value.DailyWindowStartUtc.Offset != TimeSpan.Zero ||
                value.DailyWindowEndUtc.Offset != TimeSpan.Zero || value.DailyWindowStartUtc > value.EvaluatedAtUtc ||
                value.DailyWindowEndUtc <= value.EvaluatedAtUtc || value.DailyWindowEndUtc - value.DailyWindowStartUtc > TimeSpan.FromHours(26)) ||
            value.ReasonCode == "execution_queue_dependency_not_successful" && step.DependsOn.Count == 0 ||
            participant.GetProperty("kind").GetString() != "database" ||
            participant.GetProperty("resourceKey").GetString() != "automation:" + step.ScriptId) throw Invalid();
        return value;
    }

    internal static bool IsParticipant(JsonElement participant)
    {
        if (!Matches(participant)) return false;
        _ = ReadShape(participant); return true;
    }

    private static ExecutionBindingSkip ReadShape(JsonElement participant)
    {
        var identity = participant.GetProperty("identity");
        var value = identity.Deserialize<ExecutionBindingSkip>(StepResultCodec.Options) ?? throw Invalid();
        if (!JsonElement.DeepEquals(identity, JsonSerializer.SerializeToElement(value, StepResultCodec.Options)) ||
            value.Format != Format || value.Version != 1 || string.IsNullOrWhiteSpace(value.StepId) || value.StepId.Length > 120 ||
            value.EvaluatedAtUtc == default || value.EvaluatedAtUtc.Offset != TimeSpan.Zero || value.SuccessfulRunsToday < 0 ||
            value.ReasonCode is not ("execution_daily_success_limit" or "execution_queue_dependency_not_successful") ||
            participant.GetProperty("kind").GetString() != "database") throw Invalid();
        return value;
    }

    internal static ImmutableArray<ExecutionBindingSkip> Read(JsonElement journal, NexusPipeline.Platform.Serialization.Protocol.ExecutionPlan total)
    {
        var values = journal.GetProperty("participants").EnumerateArray().Where(Matches).Select(item => Decode(item, total)).ToImmutableArray();
        if (values.Select(value => value.StepId).Distinct(StringComparer.Ordinal).Count() != values.Length ||
            values.Any(value => journal.GetProperty("participants").EnumerateArray().Any(participant =>
                WriterIntentCodec.IsParticipant(participant) &&
                participant.GetProperty("identity").GetProperty("stepId").GetString() == value.StepId))) throw Invalid();
        return values;
    }

    internal static JsonObject Participant(ExecutionBindingSkip value, string scriptId) => new()
    {
        ["participantId"] = Guid.NewGuid().ToString("D"), ["kind"] = "database", ["resourceKey"] = "automation:" + scriptId,
        ["identity"] = JsonSerializer.SerializeToNode(value, StepResultCodec.Options)
    };

    internal static string[] Processed(JsonElement journal, NexusPipeline.Platform.Serialization.Protocol.ExecutionPlan total,
        IReadOnlyList<StepWriterState> jobs)
    {
        var represented = jobs.Select(job => job.StepId).Concat(Read(journal, total).Select(value => value.StepId)).ToHashSet(StringComparer.Ordinal);
        var ordered = total.Steps.TakeWhile(step => represented.Contains(step.StepId)).Select(step => step.StepId).ToArray();
        if (ordered.Length != represented.Count) throw Invalid();
        return ordered;
    }

    internal static ExecutionStepFact Fact(ExecutionBindingSkip value) => new(value.StepId,
        EngineOutcome.NotStarted, BusinessOutcome.NotApplicable, value.ReasonCode);
    private static JsonException Invalid() => new("execution_binding_skip_invalid");
}
