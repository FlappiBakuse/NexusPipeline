using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record StepResultIntent(string Format, int Version, StepEvidenceBinding Binding, string Stage,
    BlobReference Artifact, EngineOutcome Engine, BusinessOutcome Business, long Generation, string ReasonCode);
internal sealed record StepResultState(StepEvidenceBinding Binding, StepResultIntent Latest, bool IsCollected);
internal enum StepCompletionKind { Pending, Registered, Collected, NotDispatched }
internal sealed record ExecutionStepCompletion(string StepId, StepCompletionKind Kind,
    StepResultState? Result, string? NotDispatchedReason);
internal sealed record ExecutionUndispatchedSteps(string Format, int Version, Guid ParentJournalId, Guid ParentOperationId,
    string PlanDigest, Guid DispatchOperationId, string ReasonCode, ImmutableArray<string> StepIds);

internal static class StepResultCodec
{
    internal const string Format = "nexus.execution.step-result";
    internal const string UndispatchedFormat = "nexus.execution.not-dispatched";
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal static bool IsParticipant(JsonElement participant)
    {
        var identity = participant.GetProperty("identity");
        if (!identity.TryGetProperty("format", out var format) || format.GetString() != Format) return false;
        _ = Decode(participant); return true;
    }

    internal static ImmutableArray<StepResultState> Read(JsonElement journal, ExecutionPlan total)
    {
        if (AttemptReadScope.TryRead<StepResultState>(journal, total, out var saved)) return saved;
        var original = journal;
        journal = ExecutionInvocationCodec.Current(journal);
        var jobs = WriterIntentCodec.Read(journal, total).ToDictionary(item => item.StepId);
        var results = new Dictionary<string, StepResultState>(StringComparer.Ordinal);
        var observations = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var participant in journal.GetProperty("participants").EnumerateArray().Where(IsParticipant))
        {
            var item = Decode(participant); var binding = item.Binding;
            var step = total.Steps.SingleOrDefault(value => value.StepId == binding.StepId) ?? throw Invalid();
            if (!jobs.TryGetValue(binding.StepId, out var job) || binding != Binding(journal, job) ||
                participant.GetProperty("resourceKey").GetString() != "automation:" + step.ScriptId) throw Invalid();
            if (results.TryGetValue(binding.StepId, out var prior))
            {
                if (prior.IsCollected || prior.Binding != binding || item.Generation < prior.Latest.Generation ||
                    prior.Latest.Engine is not (EngineOutcome.Running or EngineOutcome.Unknown) && item.Engine != prior.Latest.Engine ||
                    prior.Latest.Business != BusinessOutcome.Unverified && item.Business != prior.Latest.Business) throw Invalid();
            }
            if (item.Stage == "observed")
            {
                var count = observations.GetValueOrDefault(binding.StepId) + 1; observations[binding.StepId] = count;
                if (count > 2 || item.Business == BusinessOutcome.Unverified && item.Engine is EngineOutcome.Running or EngineOutcome.Unknown) throw Invalid();
            }
            else if (job.ClosedElapsedMilliseconds is null || item.Engine == EngineOutcome.Running) throw Invalid();
            results[binding.StepId] = new(binding, item, item.Stage == "collected");
        }
        return AttemptReadScope.Remember(original, total,
            total.Steps.Where(step => results.ContainsKey(step.StepId)).Select(step => results[step.StepId]).ToImmutableArray());
    }

    internal static bool IsUndispatchedParticipant(JsonElement participant)
    {
        var identity = participant.GetProperty("identity");
        if (!identity.TryGetProperty("format", out var format) || format.GetString() != UndispatchedFormat) return false;
        _ = DecodeUndispatched(participant); return true;
    }

    internal static ExecutionUndispatchedSteps? ReadUndispatched(JsonElement journal, ExecutionPlan total)
    {
        var participants = journal.GetProperty("participants").EnumerateArray().Where(IsUndispatchedParticipant).ToArray();
        if (participants.Length == 0) return null;
        if (participants.Length != 1) throw Invalid();
        var item = DecodeUndispatched(participants[0]); var close = DispatchIntentCodec.Read(journal, total);
        var jobs = BindingSkipCodec.Processed(journal, total, WriterIntentCodec.ReadJobs(journal, total));
        if (close is null || item.ParentJournalId != journal.GetProperty("journalId").GetGuid() ||
            item.ParentOperationId != journal.GetProperty("operationId").GetGuid() || item.PlanDigest != journal.GetProperty("planDigest").GetString() ||
            item.DispatchOperationId != close.CancellationOperationId || item.ReasonCode != close.ReasonCode ||
            jobs.Length >= total.Steps.Count ||
            !item.StepIds.SequenceEqual(total.Steps.Skip(jobs.Length).Select(step => step.StepId)) ||
            participants[0].GetProperty("resourceKey").GetString() != "automation:" + total.Steps[jobs.Length].ScriptId) throw Invalid();
        return item;
    }

    internal static ImmutableArray<ExecutionStepCompletion> Coverage(JsonElement journal, ExecutionPlan total)
    {
        if (AttemptReadScope.TryRead<ExecutionStepCompletion>(journal, total, out var saved)) return saved;
        var writers = WriterIntentCodec.ReadJobs(journal, total);
        var jobs = writers.Select(job => job.StepId).ToHashSet(StringComparer.Ordinal);
        var results = Read(journal, total).ToDictionary(item => item.Binding.StepId);
        var suffix = ReadUndispatched(journal, total);
        var skipped = BindingSkipCodec.Read(journal, total).ToDictionary(item => item.StepId);
        var suppressed = BindingSuppressionCodec.Read(journal, total).ToDictionary(item => item.StepId);
        return AttemptReadScope.Remember(journal, total, total.Steps.Select(step =>
        {
            results.TryGetValue(step.StepId, out var result);
            if (skipped.TryGetValue(step.StepId, out var skip))
                return new ExecutionStepCompletion(step.StepId, StepCompletionKind.Collected, null, skip.ReasonCode);
            if (jobs.Contains(step.StepId))
                return ExecutionBindingRoles.IsComplete(journal, total, step.StepId, writers, results)
                    ? new ExecutionStepCompletion(step.StepId, StepCompletionKind.Collected, result,
                        suppressed.GetValueOrDefault(step.StepId)?.ReasonCode)
                    : new(step.StepId, StepCompletionKind.Registered, result, null);
            return suffix is not null && suffix.StepIds.Contains(step.StepId)
                ? new(step.StepId, StepCompletionKind.NotDispatched, null, suffix.ReasonCode)
                : new(step.StepId, StepCompletionKind.Pending, null, null);
        }).ToImmutableArray());
    }

    internal static ImmutableArray<ExecutionStepFact> CompletedFacts(JsonElement journal, ExecutionPlan total, string? totalPlanSha256 = null) => Coverage(journal, total)
        .Select(item => item.Kind switch
        {
            StepCompletionKind.Collected => ExecutionBindingRoles.Combine(journal, total, item, totalPlanSha256),
            StepCompletionKind.NotDispatched => new ExecutionStepFact(item.StepId, EngineOutcome.NotStarted, BusinessOutcome.Unverified, item.NotDispatchedReason!),
            _ => throw new IOException("attempt_workers_uncollected")
        }).ToImmutableArray();

    internal static JsonObject UndispatchedParticipant(ExecutionUndispatchedSteps item, string scriptId) => new()
    {
        ["participantId"] = Guid.NewGuid().ToString("D"), ["kind"] = "database", ["resourceKey"] = "automation:" + scriptId,
        ["identity"] = JsonSerializer.SerializeToNode(item, Options)
    };

    private static ExecutionUndispatchedSteps DecodeUndispatched(JsonElement participant)
    {
        var identity = participant.GetProperty("identity"); var item = identity.Deserialize<ExecutionUndispatchedSteps>(Options) ?? throw Invalid();
        if (!JsonElement.DeepEquals(identity, JsonSerializer.SerializeToElement(item, Options)) || item.Format != UndispatchedFormat || item.Version != 1 ||
            item.ParentJournalId == Guid.Empty || item.ParentOperationId == Guid.Empty || item.DispatchOperationId == Guid.Empty ||
            !FileTreePolicy.Digest(item.PlanDigest) || item.StepIds.IsDefaultOrEmpty || item.StepIds.Length > 4096 ||
            item.StepIds.Any(string.IsNullOrWhiteSpace) || item.StepIds.Distinct(StringComparer.Ordinal).Count() != item.StepIds.Length ||
            item.ReasonCode is not (DispatchIntentCodec.Reason or DispatchIntentCodec.CompletionReason or
                DispatchIntentCodec.InterruptionReason or DispatchIntentCodec.BudgetReason or DispatchIntentCodec.TargetReason) ||
            participant.GetProperty("kind").GetString() != "database") throw Invalid();
        return item;
    }

    internal static StepEvidenceBinding Binding(JsonElement journal, StepWriterState job) => new(
        journal.GetProperty("instanceId").GetGuid(), journal.GetProperty("journalId").GetGuid(), journal.GetProperty("operationId").GetGuid(),
        journal.GetProperty("runId").GetGuid(), journal.GetProperty("attemptId").GetGuid(), job.StepId, job.JobName, job.WriterId, job.SessionId,
        journal.GetProperty("planDigest").GetString()!);

    internal static IReadOnlyList<StepResultIntent> ReadAll(JsonElement journal) => journal.GetProperty("participants")
        .EnumerateArray().Where(IsParticipant).Select(Decode).ToArray();

    internal static JsonObject Participant(StepResultIntent item, string scriptId) => new()
    {
        ["participantId"] = Guid.NewGuid().ToString("D"), ["kind"] = "database", ["resourceKey"] = "automation:" + scriptId,
        ["identity"] = JsonSerializer.SerializeToNode(item, Options)
    };

    private static StepResultIntent Decode(JsonElement participant)
    {
        var identity = participant.GetProperty("identity"); var item = identity.Deserialize<StepResultIntent>(Options) ?? throw Invalid();
        if (!JsonElement.DeepEquals(identity, JsonSerializer.SerializeToElement(item, Options)) || item.Format != Format || item.Version != 1 ||
            item.Binding is null || item.Artifact is null || item.Stage is not ("observed" or "collected") ||
            !Enum.IsDefined(item.Engine) || !Enum.IsDefined(item.Business) || item.Generation < 0 || item.Generation > 9007199254740991 ||
            string.IsNullOrWhiteSpace(item.ReasonCode) || item.ReasonCode.Length > 120 || item.ReasonCode.Any(char.IsControl) ||
            !FileTreePolicy.Digest(item.Artifact.Sha256) || item.Artifact.RelativePath != $"blobs/plans/{item.Artifact.Sha256}.json" ||
            item.Artifact.SizeBytes is < 1 or > StrictJson.MaximumBytes || participant.GetProperty("kind").GetString() != "database") throw Invalid();
        return item;
    }

    private static JsonException Invalid() => new("execution_step_result_unrecognized");
}
