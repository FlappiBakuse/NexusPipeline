using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record InheritedHookSource(Guid OperationId, Guid JournalId, BlobReference Attempt,
    HookResultIntent Result)
{
    public int? InvocationOrdinal { get; init; }
}
internal sealed record PostHookEvaluation(ExecutionStepFact Main, RetryEvidence Evidence, ExecutionBudgetSnapshot Budget,
    DateTimeOffset ObservedAtUtc, PostDeferralDecision Decision)
{
    public int? InvocationOrdinal { get; init; }
    public long? BindingConsumedMilliseconds { get; init; }
}
internal sealed record RetryHookIntent(string Format, int Version, Guid RunId, Guid AttemptId, Guid ParentJournalId,
    string StepId, string Role, string Disposition, long BeforeRevision, InheritedHookSource? Source,
    BlobReference? MainArtifact, PostHookEvaluation? Evaluation);

internal static class RetryHookCodec
{
    internal const string Format = "nexus.execution.retry-hook";

    internal static bool IsParticipant(JsonElement participant)
    {
        var identity = participant.GetProperty("identity");
        if (!identity.TryGetProperty("format", out var format) || format.GetString() != Format) return false;
        _ = Decode(participant); return true;
    }

    internal static ImmutableArray<RetryHookIntent> Read(JsonElement journal, ExecutionPlan total)
    {
        journal = ExecutionInvocationCodec.Current(journal);
        var result = ImmutableArray.CreateBuilder<RetryHookIntent>();
        var jobs = WriterIntentCodec.ReadJobs(journal, total);
        foreach (var participant in journal.GetProperty("participants").EnumerateArray().Where(IsParticipant))
        {
            var item = Decode(participant);
            var step = total.Steps.SingleOrDefault(step => step.StepId == item.StepId) ?? throw Invalid();
            var hook = ExecutionBindingRoles.Hook(step, item.Role);
            if (item.RunId.ToString("D") != total.RunId || item.ParentJournalId != journal.GetProperty("journalId").GetGuid() ||
                item.AttemptId != journal.GetProperty("attemptId").GetGuid() ||
                item.BeforeRevision >= journal.GetProperty("revision").GetInt64() ||
                result.Any(prior => prior.StepId == item.StepId && prior.Role == item.Role) ||
                jobs.Any(job => job.StepId == item.StepId && job.Role == item.Role) ||
                JsonSerializer.SerializeToUtf8Bytes(participant, StepResultCodec.Options).Length > ExecutionHookInputs.MaximumJournalEventBytes ||
                item.Role == "preHook" && !hook.OnceAfterSuccess || item.Role == "postHook" && !hook.FinalOnly ||
                participant.GetProperty("resourceKey").GetString() != "automation:" + step.ScriptId) throw Invalid();
            result.Add(item);
        }
        return result.ToImmutable();
    }

    internal static ImmutableArray<string> ResolvedRoles(JsonElement journal, ExecutionPlan total, string stepId) =>
        Read(journal, total).Where(item => item.StepId == stepId).Select(item => item.Role).ToImmutableArray();

    internal static JsonObject Participant(RetryHookIntent value, string scriptId) => new()
    {
        ["participantId"] = Guid.NewGuid().ToString("D"), ["kind"] = "database", ["resourceKey"] = "automation:" + scriptId,
        ["identity"] = JsonSerializer.SerializeToNode(value, StepResultCodec.Options)
    };

    private static RetryHookIntent Decode(JsonElement participant)
    {
        var value = participant.GetProperty("identity");
        var item = value.Deserialize<RetryHookIntent>(StepResultCodec.Options) ?? throw Invalid();
        if (!JsonElement.DeepEquals(value, JsonSerializer.SerializeToElement(item, StepResultCodec.Options)) ||
            item.Format != Format || item.Version != 1 || item.RunId == Guid.Empty || item.AttemptId == Guid.Empty ||
            item.ParentJournalId == Guid.Empty || string.IsNullOrWhiteSpace(item.StepId) || item.StepId.Length > 120 ||
            item.BeforeRevision is < 1 or > 4095 || participant.GetProperty("kind").GetString() != "database") throw Invalid();
        if (item.Role == "preHook")
        {
            if (item.Disposition != "inheritedSuccess" || item.Source is not { } source || source.Result is null || source.Result.Binding is null || source.Attempt is null || source.Result.Artifact is null || item.MainArtifact is not null || item.Evaluation is not null ||
                source.OperationId == Guid.Empty || source.JournalId == Guid.Empty || source.Result.Role != "preHook" ||
                source.Result.Engine != EngineOutcome.Succeeded || source.Result.StepId != item.StepId ||
                source.Result.Binding.ParentOperationId != source.OperationId || source.Result.Binding.ParentJournalId != source.JournalId ||
                source.Result.Binding.RunId != item.RunId || source.InvocationOrdinal is < 1 or > 98 ||
                source.InvocationOrdinal is null && source.Result.Binding.AttemptId == item.AttemptId ||
                source.InvocationOrdinal is not null && (source.Result.Binding.AttemptId != item.AttemptId || source.JournalId != item.ParentJournalId))
                throw Invalid();
            StepEvidenceCodec.Plan(source.Attempt); StepEvidenceCodec.Plan(source.Result.Artifact);
        }
        else if (item.Role == "postHook")
        {
            if (item.Disposition != "deferred" || item.Source is not null || item.MainArtifact is null || item.Evaluation is not { } evaluation || evaluation.Main is null || evaluation.Evidence is null || evaluation.Budget is null || evaluation.Decision is null ||
                evaluation.Main.StepId != item.StepId ||
                evaluation.ObservedAtUtc.Offset != TimeSpan.Zero || !evaluation.Decision.ShouldDefer)
                throw Invalid();
            StepEvidenceCodec.Plan(item.MainArtifact);
        }
        else throw Invalid();
        return item;
    }

    internal static JsonException Invalid() => new("execution_retry_hook_unrecognized");
}
