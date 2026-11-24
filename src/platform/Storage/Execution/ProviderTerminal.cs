using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record ProviderTerminalIntent(string Format, int Version, StepEvidenceBinding Binding,
    Guid ActivationId, string EntryId, long BeforeRevision, BlobReference Artifact, ExecutionProviderDecision Decision);

internal static class ProviderTerminalCodec
{
    internal const string Format = "nexus.execution.provider-terminal";

    internal static bool IsParticipant(JsonElement participant)
    {
        if (!ExecutionParticipantText.MatchesFormat(participant, Format)) return false;
        _ = Decode(participant); return true;
    }

    internal static ImmutableArray<ProviderTerminalIntent> Read(AttemptJournalView journal, ExecutionPlan total)
    {
        journal = ExecutionInvocationCodec.Current(journal);
        var jobs = WriterIntentCodec.Read(journal, total).ToDictionary(item => item.StepId);
        var result = ImmutableArray.CreateBuilder<ProviderTerminalIntent>();
        var closed = false;
        foreach (var participant in journal.Participants)
        {
            if (DispatchIntentCodec.IsParticipant(participant)) closed = true;
            if (!ExecutionParticipantText.MatchesFormat(participant, Format)) continue;
            var item = Decode(participant);
            var step = total.Steps.SingleOrDefault(step => step.StepId == item.Binding.StepId) ?? throw Invalid();
            if (closed || !jobs.TryGetValue(step.StepId, out var job) ||
                StepResultCodec.Binding(journal, job) != item.Binding ||
                step.EngineKind != "extensionWorker" || step.Provider is null ||
                Guid.Parse(step.Provider.ActivationId) != item.ActivationId || step.Provider.EntryId != item.EntryId ||
                participant.GetProperty("resourceKey").GetString() != "automation:" + step.ScriptId ||
                item.BeforeRevision >= journal.Root.GetProperty("revision").GetInt64() ||
                result.Any(prior => prior.Binding.StepId == step.StepId)) throw Invalid();
            result.Add(item);
        }
        return result.ToImmutable();
    }

    internal static ImmutableArray<ProviderTerminalIntent> ReadHistory(AttemptJournalView journal, ExecutionPlan total)
    {
        foreach (var view in ExecutionInvocationCodec.Views(journal)) _ = Read(view, total);
        return journal.Participants.Where(participant => ExecutionParticipantText.MatchesFormat(participant, Format))
            .Select(Decode).ToImmutableArray();
    }

    internal static JsonObject Participant(ProviderTerminalIntent value, string scriptId) => new()
    {
        ["participantId"] = Guid.NewGuid().ToString("D"), ["kind"] = "process", ["resourceKey"] = "automation:" + scriptId,
        ["identity"] = JsonSerializer.SerializeToNode(value, StepResultCodec.Options)
    };

    private static ProviderTerminalIntent Decode(JsonElement participant)
    {
        var json = participant.GetProperty("identity");
        var value = json.Deserialize<ProviderTerminalIntent>(StepResultCodec.Options) ?? throw Invalid();
        if (!JsonElement.DeepEquals(json, JsonSerializer.SerializeToElement(value, StepResultCodec.Options)) ||
            value.Format != Format || value.Version != 1 || value.Binding is null || value.ActivationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(value.EntryId) || value.BeforeRevision < 0 || value.Decision is null ||
            value.Decision.Sequence is < 2 or > ProviderProtocolState.MaximumEvents ||
            value.Decision.Engine is not (EngineOutcome.Succeeded or EngineOutcome.Failed or EngineOutcome.Cancelled) ||
            value.Decision.ReasonCode is not { Length: >= 1 and <= 80 } ||
            value.Decision.ReasonCode[0] is not (>= 'a' and <= 'z') ||
            value.Decision.ReasonCode.Any(character => character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_')) ||
            participant.GetProperty("kind").GetString() != "process") throw Invalid();
        StepEvidenceCodec.Plan(value.Artifact);
        return value;
    }

    private static IOException Invalid() => new("execution_provider_terminal_unrecognized");
}

[SupportedOSPlatform("windows")]
internal sealed class ProviderTerminalStore(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease)
{
    internal void Record(PreparedProviderTerminal proof)
    {
        lease.RequirePublicationEntry();
        proof.Use(proof.Binding, (artifact, decision) => lease.PublishExecution(() =>
        {
            lease.VerifyHeld(instance);
            var state = AttemptWriterState.Read(instance, database, contracts, proof.Binding.ParentJournalId);
            var prior = ProviderTerminalCodec.Read(state.Latest.Root, state.Total)
                .SingleOrDefault(item => item.Binding.StepId == proof.Binding.StepId);
            if (prior is not null)
            {
                if (prior.Binding != proof.Binding || prior.Artifact != artifact || prior.Decision != decision)
                    throw new IOException("execution_provider_terminal_changed");
                ExecutionAttemptProjection.Synchronize(instance, database, contracts, state);
                return true;
            }
            if (state.Journal.Phase != JournalPhase.Applying || state.Gate.State != EffectGateState.Entered ||
                state.Latest.Revision != state.Journal.Revision || state.IsDispatchClosed)
                throw new IOException("execution_provider_admission_closed");
            var step = state.Total.Steps.Single(step => step.StepId == proof.Binding.StepId);
            var provider = step.Provider ?? throw new IOException("execution_provider_terminal_unbound");
            var replay = new ProviderControlReader(instance, database, contracts).Read(proof.Binding, artifact);
            if (replay.Terminal != decision) throw new IOException("execution_provider_terminal_unbound");
            var intended = new ProviderTerminalIntent(ProviderTerminalCodec.Format, 1, proof.Binding,
                Guid.Parse(provider.ActivationId), provider.EntryId, state.Journal.Revision, artifact, decision);
            TargetJournalPublisher.AppendParticipant(instance, database, contracts, state,
                ProviderTerminalCodec.Participant(intended, step.ScriptId));
            return true;
        }));
    }
}

internal sealed record ProviderTerminalAdmission(long Revision, ProviderTerminalIntent Item);

internal static class ProviderTerminalProjection
{
    internal static IReadOnlyList<ProviderTerminalAdmission> Read(RuntimeInstance instance, StateDatabase database,
        ContractReader contracts, AttemptWriterState state, JournalDocument projected, IReadOnlyList<JournalDocument> tail)
    {
        var prior = ProviderTerminalCodec.ReadHistory(projected.Root, state.Total);
        var results = new List<ProviderTerminalAdmission>();
        foreach (var next in tail)
        {
            var current = ProviderTerminalCodec.ReadHistory(next.Root, state.Total);
            foreach (var added in current.Skip(prior.Length))
            {
                if (next.Phase != JournalPhase.Applying || added.BeforeRevision != next.Revision - 1)
                    throw new IOException("execution_provider_publication_unbound");
                var replay = new ProviderControlReader(instance, database, contracts).Read(added.Binding, added.Artifact);
                if (replay.Terminal != added.Decision) throw new IOException("execution_provider_terminal_unbound");
                results.Add(new(next.Revision, added));
            }
            if (!current.Take(prior.Length).SequenceEqual(prior)) throw new IOException("execution_provider_history_changed");
            prior = current;
        }
        return results.AsReadOnly();
    }

    internal static void RequireInside(SqliteConnection connection, SqliteTransaction transaction, AttemptWriterState state,
        ProviderTerminalAdmission admission)
    {
        if (state.Journal.Phase != JournalPhase.Applying || state.Gate.State != EffectGateState.Entered)
            throw new IOException("execution_provider_admission_closed");
        ExecutionCancellationRows.RequireDispatchOpen(connection, transaction, state.Journal.JournalId);
        var binding = admission.Item.Binding;
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM main.journal_writer_job_roots r JOIN main.journal_writer_jobs j " +
            "ON j.journal_id=r.journal_id AND j.writer_id=r.writer_id AND j.job_name=r.job_name " +
            "WHERE r.journal_id=$journal AND r.writer_id=$writer AND r.job_name=$job AND j.session_id=$session);";
        command.Parameters.AddWithValue("$journal", binding.ParentJournalId.ToString("D"));
        command.Parameters.AddWithValue("$writer", binding.WriterId.ToString("D"));
        command.Parameters.AddWithValue("$job", binding.JobName); command.Parameters.AddWithValue("$session", binding.SessionId);
        if ((long)command.ExecuteScalar()! != 1) throw new IOException("execution_provider_root_unbound");
    }
}

[SupportedOSPlatform("windows")]
internal sealed class PreparedProviderTerminal
{
    private readonly ProviderControlSession _owner;
    internal StepEvidenceBinding Binding { get; }
    internal BlobReference Artifact { get; }
    internal ExecutionProviderDecision Decision { get; }

    private PreparedProviderTerminal(ProviderControlSession owner, StepEvidenceBinding binding,
        BlobReference artifact, ExecutionProviderDecision decision)
    { _owner = owner; Binding = binding; Artifact = artifact; Decision = decision; }

    internal static PreparedProviderTerminal FromNative(ProviderControlSession owner) =>
        owner.UseTerminal((binding, artifact, decision) => new PreparedProviderTerminal(owner, binding, artifact, decision));

    internal T Use<T>(StepEvidenceBinding expected, Func<BlobReference, ExecutionProviderDecision, T> effect)
    {
        if (expected != Binding) throw new IOException("execution_provider_terminal_unbound");
        return _owner.UseTerminal((binding, artifact, decision) =>
        {
            if (binding != expected || artifact != Artifact || decision != Decision) throw new IOException("execution_provider_terminal_changed");
            return effect(artifact, decision);
        });
    }
}
