using System.Buffers;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record ExecutionInvocation(string Format, int Version, string StepId, int Ordinal,
    long BeforeRevision, string PreviousJobName, DateTimeOffset PreparedAtUtc,
    ImmutableArray<BlobReference> ConfigurationPlans)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExecutionTaskRetry? TaskRetry { get; init; }
}

internal static class ExecutionInvocationCodec
{
    internal const string Format = "nexus.execution.invocation";
    private static readonly HashSet<string> ScopedFormats = new(StringComparer.Ordinal)
    {
        WriterIntentCodec.Format, StepResultCodec.Format, HookResultCodec.Format,
        RetryHookCodec.Format, BindingSuppressionCodec.Format, ProviderTerminalCodec.Format,
        TargetJobCodec.Format, PcStageCodec.Format, AndroidControlCodec.RegistrationFormat,
        AndroidStartupCodec.Format, AndroidCollectedCodec.Format, ProviderLauncherCodec.Format,
        ExecutionCaptureCodec.Format, PreviewDomainCodec.Format, BindingTimeoutCodec.Format
    };

    internal static bool IsParticipant(JsonElement participant)
    {
        var identity = participant.GetProperty("identity");
        if (!identity.TryGetProperty("format", out var format) || format.GetString() != Format) return false;
        _ = Decode(participant); return true;
    }

    internal static ExecutionInvocation Decode(JsonElement participant)
    {
        var identity = participant.GetProperty("identity");
        var value = identity.Deserialize<ExecutionInvocation>(StepResultCodec.Options) ?? throw Invalid();
        if (!JsonElement.DeepEquals(identity, JsonSerializer.SerializeToElement(value, StepResultCodec.Options)) ||
            value.Format != Format || value.Version != 1 || string.IsNullOrWhiteSpace(value.StepId) || value.StepId.Length > 120 ||
            value.Ordinal is < 2 or > 99 || value.BeforeRevision is < 1 or > 4095 || value.PreparedAtUtc.Offset != TimeSpan.Zero ||
            value.PreviousJobName is not { Length: 50 } || !value.PreviousJobName.StartsWith("Local\\NexusWorker-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(value.PreviousJobName[18..], "N", out var job) || job == Guid.Empty ||
            value.PreviousJobName[18..] != job.ToString("N") || value.ConfigurationPlans.IsDefault || value.ConfigurationPlans.Length > 32 ||
            value.ConfigurationPlans.Distinct().Count() != value.ConfigurationPlans.Length || participant.GetProperty("kind").GetString() != "database")
            throw Invalid();
        foreach (var plan in value.ConfigurationPlans) StepEvidenceCodec.Plan(plan);
        if (value.TaskRetry is { } retry)
        {
            StepEvidenceCodec.Plan(retry.PreviousRaw); StepEvidenceCodec.Plan(retry.PreviousObservation);
            if (retry.PreviousBinding is null || retry.PreviousBinding.StepId != value.StepId || retry.PreviousBinding.JobName != value.PreviousJobName ||
                retry.Selection is null || retry.Selection.Targets.IsDefaultOrEmpty || retry.Selection.Targets.Length > 4096 ||
                retry.Selection.SelectedIds.IsDefaultOrEmpty || retry.Selection.SelectedIds.Length > 4096)
                throw Invalid();
        }
        return value;
    }

    internal static ImmutableArray<ExecutionInvocation> Read(JsonElement journal, ExecutionPlan total)
    {
        var result = ImmutableArray.CreateBuilder<ExecutionInvocation>();
        foreach (var participant in journal.GetProperty("participants").EnumerateArray().Where(IsParticipant))
        {
            var value = Decode(participant);
            var step = total.Steps.SingleOrDefault(step => step.StepId == value.StepId) ?? throw Invalid();
            var ordinal = result.Where(prior => prior.StepId == value.StepId).Select(prior => prior.Ordinal).DefaultIfEmpty(1).Max() + 1;
            if (value.Ordinal != ordinal || ordinal > (step.MaximumAttempts ?? 1) ||
                value.BeforeRevision >= journal.GetProperty("revision").GetInt64() ||
                value.ConfigurationPlans.Length != total.ConfigurationSlots.Count(slot => slot.StepId == value.StepId) ||
                participant.GetProperty("resourceKey").GetString() != "automation:" + step.ScriptId) throw Invalid();
            result.Add(value);
        }
        return result.ToImmutable();
    }

    internal static int OrdinalForJob(JsonElement journal, string stepId, string jobName)
    {
        var starts = journal.GetProperty("participants").EnumerateArray().Where(IsParticipant).Select(Decode)
            .Where(item => item.StepId == stepId).ToArray();
        return starts.SingleOrDefault(item => item.PreviousJobName == jobName)?.Ordinal - 1 ??
            starts.Select(item => item.Ordinal).DefaultIfEmpty(1).Max();
    }

    internal static int Ordinal(JsonElement journal, string stepId) => journal.GetProperty("participants").EnumerateArray()
        .Where(IsParticipant).Select(Decode).Where(item => item.StepId == stepId).Select(item => item.Ordinal).DefaultIfEmpty(1).Max();

    // Only invocation-owned events are projected. Configuration completion and unknown formats remain visible to their owners.
    internal static JsonElement Current(JsonElement journal)
    {
        var participants = journal.GetProperty("participants").EnumerateArray().ToArray();
        var starts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < participants.Length; index++)
            if (IsParticipant(participants[index])) starts[Decode(participants[index]).StepId] = index;
        if (starts.Count == 0) return journal;
        var selected = participants.Where((participant, index) =>
        {
            var step = ScopedStep(participant);
            return step is null || !starts.TryGetValue(step, out var start) || index > start;
        });
        var projected = selected.ToArray();
        return projected.Length == participants.Length ? journal : WithParticipants(journal, projected);
    }

    internal static IReadOnlyList<JsonElement> Views(JsonElement journal)
    {
        var participants = journal.GetProperty("participants").EnumerateArray().ToArray();
        var result = new List<JsonElement>();
        for (var index = 0; index < participants.Length; index++)
            if (IsParticipant(participants[index])) result.Add(WithParticipants(journal, participants.Take(index)));
        result.Add(journal); return result;
    }

    internal static JsonElement ForJob(JsonElement journal, ExecutionPlan total, string jobName) => Views(journal)
        .LastOrDefault(view => WriterIntentCodec.ReadJobs(view, total).Any(job => job.JobName == jobName)) is var view && view.ValueKind != JsonValueKind.Undefined
            ? Current(view) : throw Invalid();

    internal static ImmutableArray<RunAttemptPlanConfigurationPlansItem> Plans(JsonElement journal, ExecutionPlan total, RunAttemptPlan attempt) =>
        attempt.ConfigurationPlans.Concat(Read(journal, total).SelectMany(item => item.ConfigurationPlans.Select(plan =>
            new RunAttemptPlanConfigurationPlansItem { StepId = item.StepId,
                Plan = new() { RelativePath = plan.RelativePath, Sha256 = plan.Sha256, SizeBytes = plan.SizeBytes } }))).ToImmutableArray();

    internal static ImmutableArray<BlobReference> CurrentPlans(JsonElement journal, RunAttemptPlan attempt, string stepId)
    {
        var invocation = journal.GetProperty("participants").EnumerateArray().Where(IsParticipant).Select(Decode)
            .LastOrDefault(item => item.StepId == stepId);
        return invocation?.ConfigurationPlans ?? attempt.ConfigurationPlans.Where(item => item.StepId == stepId)
            .Select(item => AttemptPlanCodec.Reference(item.Plan)).ToImmutableArray();
    }

    internal static ImmutableArray<BlobReference> PlansForOrdinal(JsonElement journal, ExecutionPlan total, RunAttemptPlan attempt, string stepId, int ordinal) =>
        ordinal == 1 ? attempt.ConfigurationPlans.Where(item => item.StepId == stepId).Select(item => AttemptPlanCodec.Reference(item.Plan)).ToImmutableArray() :
        Read(journal, total).Single(item => item.StepId == stepId && item.Ordinal == ordinal).ConfigurationPlans;

    internal static int PlanOrdinal(JsonElement journal, ExecutionPlan total, RunAttemptPlan attempt, string stepId, BlobReference reference) =>
        attempt.ConfigurationPlans.Any(item => item.StepId == stepId && AttemptPlanCodec.Reference(item.Plan) == reference) ? 1 :
        Read(journal, total).Single(item => item.StepId == stepId && item.ConfigurationPlans.Contains(reference)).Ordinal;

    internal static JsonObject Participant(ExecutionInvocation value, string scriptId) => new()
    {
        ["participantId"] = Guid.NewGuid().ToString("D"), ["kind"] = "database", ["resourceKey"] = "automation:" + scriptId,
        ["identity"] = JsonSerializer.SerializeToNode(value, StepResultCodec.Options)
    };

    private static string? ScopedStep(JsonElement participant)
    {
        var identity = participant.GetProperty("identity");
        if (!identity.TryGetProperty("format", out var format) || !ScopedFormats.Contains(format.GetString()!)) return null;
        if (identity.TryGetProperty("stepId", out var step)) return step.GetString();
        var owner = identity.TryGetProperty("binding", out var binding) ? binding : identity.GetProperty("source");
        if (owner.TryGetProperty("primary", out var primary)) owner = primary;
        return owner.GetProperty("stepId").GetString();
    }

    private static JsonElement WithParticipants(JsonElement journal, IEnumerable<JsonElement> participants)
    {
        var bytes = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject();
            foreach (var property in journal.EnumerateObject())
            {
                if (property.Name != "participants") { property.WriteTo(writer); continue; }
                writer.WritePropertyName(property.Name); writer.WriteStartArray();
                foreach (var participant in participants) participant.WriteTo(writer);
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(bytes.WrittenMemory);
        return document.RootElement.Clone();
    }

    internal static IOException Invalid() => new("execution_invocation_unbound");
}
