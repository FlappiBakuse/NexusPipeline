using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

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
        if (!HasFormat(participant)) return false;
        _ = Decode(participant); return true;
    }

    private static bool HasFormat(JsonElement participant) =>
        participant.GetProperty("identity").TryGetProperty("format", out var format) && format.GetString() == Format;


    internal static ImmutableArray<RetryHookIntent> Read(AttemptJournalView journal, ExecutionPlan total)
    {
        journal = ExecutionInvocationCodec.Current(journal);
        var result = ImmutableArray.CreateBuilder<RetryHookIntent>();
        var jobs = WriterIntentCodec.ReadJobs(journal, total);
        foreach (var participant in journal.Participants.Where(HasFormat))
        {
            var item = Decode(participant);
            var step = total.Steps.SingleOrDefault(step => step.StepId == item.StepId) ?? throw Invalid();
            var hook = ExecutionBindingRoles.Hook(step, item.Role);
            if (item.RunId.ToString("D") != total.RunId || item.ParentJournalId != journal.Root.GetProperty("journalId").GetGuid() ||
                item.AttemptId != journal.Root.GetProperty("attemptId").GetGuid() ||
                item.BeforeRevision >= journal.Root.GetProperty("revision").GetInt64() ||
                result.Any(prior => prior.StepId == item.StepId && prior.Role == item.Role) ||
                jobs.Any(job => job.StepId == item.StepId && job.Role == item.Role) ||
                JsonSerializer.SerializeToUtf8Bytes(participant, StepResultCodec.Options).Length > ExecutionHookInputs.MaximumJournalEventBytes ||
                item.Role == "preHook" && !hook.OnceAfterSuccess || item.Role == "postHook" && !hook.FinalOnly ||
                participant.GetProperty("resourceKey").GetString() != "automation:" + step.ScriptId) throw Invalid();
            result.Add(item);
        }
        return result.ToImmutable();
    }

    internal static ImmutableArray<string> ResolvedRoles(AttemptJournalView journal, ExecutionPlan total, string stepId) =>
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

internal static class RetryHookProjection
{
    internal static void RequireInside(SqliteConnection connection, SqliteTransaction transaction,
        AttemptWriterState state, JournalDocument before, JournalDocument after)
    {
        var prior = RetryHookCodec.Read(before.Root, state.Total);
        var added = RetryHookCodec.Read(after.Root, state.Total).Where(item =>
            !prior.Any(old => old.StepId == item.StepId && old.Role == item.Role)).ToArray();
        if (added.Length > 1) throw RetryHookEvidence.Invalid();
        foreach (var item in added)
        {
            if (item.BeforeRevision != before.Revision || after.Revision != before.Revision + 1 ||
                before.Phase != after.Phase) throw RetryHookEvidence.Invalid();
            ExecutionCancellationRows.RequireDispatchOpen(connection, transaction, state.Journal.JournalId);
            var ordinal = state.Total.Steps.ToList().FindIndex(step => step.StepId == item.StepId);
            ExecutionStepAdmission.RequirePredecessorsInside(connection, transaction, state, before, ordinal);
            if (item.Role == "preHook")
            {
                if (before.Phase is not (JournalPhase.Prepared or JournalPhase.Applying) ||
                    WriterIntentCodec.ReadJobs(before.Root, state.Total).Any(job => job.StepId == item.StepId))
                    throw RetryHookEvidence.Invalid();
            }
            else RetryHookEvidence.RequirePost(state.Attempt, state.Total, before, item, state.PreviousInvocationCount(item.StepId));
        }
        RequireSavedInside(connection, transaction, state, after);
    }

    internal static void RequireSavedInside(SqliteConnection connection, SqliteTransaction transaction,
        AttemptWriterState state, JournalDocument document)
    {
        var dispositions = RetryHookCodec.Read(document.Root, state.Total);
        if (dispositions.IsEmpty) return;
        if (!state.IsExecutionStart) throw RetryHookEvidence.Invalid();
        var current = StartSourceRows.Read(connection, transaction, state.Journal.OperationId);
        var jobs = WriterIntentCodec.ReadJobs(document.Root, state.Total);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        foreach (var item in dispositions)
        {
            if (jobs.Any(job => job.StepId == item.StepId && job.Role == item.Role)) throw RetryHookEvidence.Invalid();
            if (item.Source is { } source)
            {
                if (source.InvocationOrdinal is { } invocation)
                {
                    if (source.OperationId != state.Journal.OperationId || source.JournalId != state.Journal.JournalId ||
                        source.Attempt.RelativePath != state.Journal.PlanPath || source.Attempt.Sha256 != state.Journal.PlanDigest ||
                        invocation >= ExecutionInvocationCodec.Ordinal(document.Root, item.StepId)) throw RetryHookEvidence.Invalid();
                    RequireClosure(source.JournalId, source.Result.Binding.WriterId, source.Result.Binding.JobName); continue;
                }
                var previous = StartSourceRows.Read(connection, transaction, source.OperationId);
                if (previous.RunId != current.RunId || previous.Identity.SubjectId != current.Identity.SubjectId ||
                    previous.AttemptOrdinal >= current.AttemptOrdinal || previous.JournalId != source.JournalId)
                    throw RetryHookEvidence.Invalid();
                var parent = RecoveryJournalRegistry.ReadInside(connection, transaction, source.JournalId);
                if (parent.Phase != JournalPhase.Committed || parent.PlanPath != source.Attempt.RelativePath ||
                    parent.PlanDigest != source.Attempt.Sha256) throw RetryHookEvidence.Invalid();
                var receipt = AttemptReceiptStore.ReadInside(connection, transaction, source.Result.Binding.AttemptId)
                    ?? throw RetryHookEvidence.Invalid();
                if (receipt.JournalId != parent.JournalId || receipt.Facts.RunId != current.RunId ||
                    receipt.Facts.Ordinal != previous.AttemptOrdinal) throw RetryHookEvidence.Invalid();
                RequireClosure(source.JournalId, source.Result.Binding.WriterId, source.Result.Binding.JobName);
            }
            else
                foreach (var job in jobs.Where(job => job.StepId == item.StepId))
                {
                    if (job.ClosedElapsedMilliseconds is null) throw RetryHookEvidence.Invalid();
                    RequireClosure(state.Journal.JournalId, job.WriterId, job.JobName);
                }
        }
        void RequireClosure(Guid journal, Guid writer, string job)
        {
            command.Parameters.Clear(); command.Parameters.AddWithValue("$journal", journal.ToString("D"));
            command.Parameters.AddWithValue("$writer", writer.ToString("D")); command.Parameters.AddWithValue("$job", job);
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM main.journal_writer_job_closures WHERE journal_id=$journal AND writer_id=$writer AND job_name=$job);";
            if ((long)command.ExecuteScalar()! != 1) throw RetryHookEvidence.Invalid();
        }
    }
}
