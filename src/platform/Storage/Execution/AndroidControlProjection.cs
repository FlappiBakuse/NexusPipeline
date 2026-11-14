using Microsoft.Data.Sqlite;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record AndroidRegistrationAdmission(long Revision,
    AndroidCommandRegistration Registration, AndroidCommandRequest Request);
internal sealed record AndroidCollectionAdmission(long Revision, AndroidCollectedIntent Collection);
internal sealed record AndroidStartupAdmission(long Revision, AndroidStartupIntent Startup);
internal sealed record AndroidControlAdmissions(IReadOnlyList<AndroidRegistrationAdmission> Registrations,
    IReadOnlyList<AndroidCollectionAdmission> Collections, IReadOnlyList<AndroidStartupAdmission> Startups);

internal static class AndroidControlProjection
{
    internal static AndroidControlAdmissions Read(RuntimeInstance instance, StateDatabase database,
        ContractReader contracts, AttemptWriterState state, JournalDocument projected, IReadOnlyList<JournalDocument> tail)
    {
        var registrations = new List<AndroidRegistrationAdmission>();
        var collections = new List<AndroidCollectionAdmission>();
        var startups = new List<AndroidStartupAdmission>();
        var prior = AndroidControlCodec.Read(projected.Root, state.Total, state.Attempt.ExecutionPlan.Sha256);
        var oldCollections = AndroidCollectedCodec.Read(projected.Root, state.Total, state.Attempt.ExecutionPlan.Sha256);
        var oldStartups = AndroidStartupCodec.Read(projected.Root, state.Total, state.Attempt.ExecutionPlan.Sha256);
        var reader = new AndroidControlEvidence(instance, database, contracts);
        var results = new AndroidResultEvidence(instance, database, contracts);
        foreach (var next in tail)
        {
            var current = AndroidControlCodec.Read(next.Root, state.Total, state.Attempt.ExecutionPlan.Sha256);
            var finished = AndroidCollectedCodec.Read(next.Root, state.Total, state.Attempt.ExecutionPlan.Sha256);
            var started = AndroidStartupCodec.Read(next.Root, state.Total, state.Attempt.ExecutionPlan.Sha256);
            if (!current.Take(prior.Length).SequenceEqual(prior) || !SamePrefix(finished, oldCollections) || !SamePrefix(started, oldStartups))
                throw Invalid();
            foreach (var item in current.Skip(prior.Length))
                registrations.Add(new(next.Revision, item, reader.Request(state, item)));
            var nextState = state with { Latest = next };
            foreach (var item in started.Skip(oldStartups.Length))
            {
                _ = results.RequireStartup(nextState, item, verifyContent: true);
                startups.Add(new(next.Revision, item));
            }
            foreach (var item in finished.Skip(oldCollections.Length))
            {
                _ = results.RequireCollected(nextState, item, verifyContent: true);
                collections.Add(new(next.Revision, item));
            }
            prior = current; oldCollections = finished; oldStartups = started;
        }
        return new(registrations, collections, startups);
    }

    internal static void RequireInside(SqliteConnection connection, SqliteTransaction transaction,
        AttemptWriterState state, JournalDocument before, JournalDocument next, AndroidControlAdmissions prepared)
    {
        var beforeState = state with { Latest = before, Journal = state.Journal with { Revision = before.Revision, Phase = before.Phase } };
        var additions = prepared.Registrations.Where(item => item.Revision == next.Revision).ToArray();
        var completed = prepared.Collections.Where(item => item.Revision == next.Revision).ToArray();
        var startups = prepared.Startups.Where(item => item.Revision == next.Revision).ToArray();
        var count = additions.Length + completed.Length + startups.Length;
        if (count > 1 || count > 0 && (before.Phase != JournalPhase.Applying || next.Phase != before.Phase))
            throw Invalid();
        foreach (var item in additions)
        {
            if (AndroidCollectedCodec.Read(before.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)
                .Any(value => value.Binding.StepId == item.Registration.Binding.StepId) ||
                !AndroidControlAdmission.IsCleanup(item.Registration.Purpose) &&
                    AndroidStartupCodec.Read(before.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)
                        .Any(value => value.Binding.StepId == item.Registration.Binding.StepId)) throw Invalid();
            AndroidControlRows.RegisterInside(connection, transaction, beforeState, item.Registration, item.Request);
        }
        foreach (var item in completed)
        {
            AttemptWriterState.RequireCursor(connection, transaction, beforeState.Journal, EffectGateState.Entered);
            AndroidControlAdmission.RequireCollectedMain(beforeState, item.Collection.Binding.StepId);
        }
        foreach (var item in startups)
        {
            AttemptWriterState.RequireCursor(connection, transaction, beforeState.Journal, EffectGateState.Entered);
            var step = item.Startup.Binding.StepId;
            var main = beforeState.Jobs.Single(job => job.StepId == step && job.Role == "process");
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM main.journal_writer_job_roots WHERE journal_id=$journal AND job_name=$main);";
            command.Parameters.AddWithValue("$journal", state.Journal.JournalId.ToString("D")); command.Parameters.AddWithValue("$main", main.JobName);
            if ((long)command.ExecuteScalar()! != 0) throw Invalid();
            if (item.Startup.Outcome == TargetExecutionOutcome.StoppedByRequest &&
                ExecutionCancellationRows.Read(connection, transaction, beforeState.Journal, beforeState.Attempt)?.ReasonCode != item.Startup.ReasonCode) throw Invalid();
        }
    }

    internal static void RequireMainStartInside(SqliteConnection connection, SqliteTransaction transaction,
        AttemptWriterState state, StepWriterState writer)
    {
        if (writer.Role is not ("process" or "judgement") || state.Total.Steps.Single(item => item.StepId == writer.StepId).Target?.Kind != "android") return;
        var startup = AndroidStartupCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)
            .SingleOrDefault(item => item.Binding.StepId == writer.StepId);
        if (startup?.Outcome is not (TargetExecutionOutcome.ControlledReady or TargetExecutionOutcome.Skipped) ||
            AndroidCollectedCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256).Any(item => item.Binding.StepId == writer.StepId) ||
            AndroidControlCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)
                .Any(item => item.Binding.StepId == writer.StepId && AndroidControlAdmission.IsCleanup(item.Purpose))) throw Invalid();
        PcStageProjection.RequireConfigurationInstalledInside(connection, transaction, state, writer.StepId);
    }

    private static bool SamePrefix<T>(IReadOnlyList<T> current, IReadOnlyList<T> prior) => current.Count >= prior.Count &&
        current.Take(prior.Count).Zip(prior).All(pair => JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(pair.First, StepResultCodec.Options),
            JsonSerializer.SerializeToElement(pair.Second, StepResultCodec.Options)));

    private static IOException Invalid() => new("execution_android_command_projection_unbound");
}
