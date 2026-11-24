using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record PcStageBinding(Guid InstanceId, Guid ParentJournalId, Guid ParentOperationId,
    Guid AttemptId, string StepId, string TotalPlanSha256);
internal sealed record PcStageIntent(string Format, int Version, PcStageBinding Binding,
    TargetExecutionOutcome Outcome, string ReasonCode, BlobReference Artifact);
internal sealed record PcStageDocument(string Format, int Version, PcStageBinding Binding,
    TargetExecutionOutcome Outcome, string ReasonCode, string ReadinessMode, ImmutableArray<ProcessIdentity> ReadyMembers,
    ExecutionTargetBinding? TargetJob, BlobReference? TargetEvidence, long ElapsedMilliseconds);

internal static class PcStageCodec
{
    internal const string Format = "nexus.execution.pc-target-stage";

    internal static bool IsParticipant(JsonElement participant)
    {
        if (!ExecutionParticipantText.MatchesFormat(participant, Format)) return false;
        _ = Decode(participant); return true;
    }

    internal static ImmutableArray<PcStageIntent> Read(AttemptJournalView journal, ExecutionPlan total, string totalSha256) => ReadCore(journal, total, totalSha256);

    internal static ImmutableArray<PcStageIntent> ReadStructure(AttemptJournalView journal, ExecutionPlan total) => ReadCore(journal, total, null);

    private static ImmutableArray<PcStageIntent> ReadCore(AttemptJournalView journal, ExecutionPlan total, string? totalSha256)
    {
        journal = ExecutionInvocationCodec.Current(journal);
        var values = ImmutableArray.CreateBuilder<PcStageIntent>();
        foreach (var participant in journal.Participants)
        {
            if (!ExecutionParticipantText.MatchesFormat(participant, Format)) continue;
            var item = Decode(participant);
            var binding = item.Binding;
            var step = total.Steps.SingleOrDefault(step => step.StepId == binding.StepId) ?? throw Invalid();
            if (binding.InstanceId.ToString("D") != total.InstanceId || binding.ParentJournalId.ToString("D") != journal.Root.GetProperty("journalId").GetString() ||
                binding.ParentOperationId.ToString("D") != journal.Root.GetProperty("operationId").GetString() ||
                binding.AttemptId.ToString("D") != journal.Root.GetProperty("attemptId").GetString() || totalSha256 is not null && binding.TotalPlanSha256 != totalSha256 ||
                step.Target is not { Kind: "pc", Pc: not null, Android: null } || values.Any(value => value.Binding.StepId == binding.StepId) ||
                participant.GetProperty("resourceKey").GetString() != "automation:" + step.ScriptId ||
                step.Target.LaunchByHost && item.Outcome == TargetExecutionOutcome.Skipped ||
                !step.Target.LaunchByHost && item.Outcome is not (TargetExecutionOutcome.Skipped or TargetExecutionOutcome.StoppedByRequest)) throw Invalid();
            values.Add(item);
        }
        return values.ToImmutable();
    }

    internal static string Reason(TargetExecutionOutcome outcome) => outcome switch
    {
        TargetExecutionOutcome.Skipped => "execution_target_launch_skipped",
        TargetExecutionOutcome.ExternalReady => "execution_target_external_ready",
        TargetExecutionOutcome.OwnedReady => "execution_target_owned_ready",
        TargetExecutionOutcome.LaunchFailed => "execution_target_launch_failed",
        TargetExecutionOutcome.ReadinessTimedOut => "execution_target_readiness_timeout",
        TargetExecutionOutcome.Blocked or TargetExecutionOutcome.ReadinessUnknown => "execution_target_readiness_unverified",
        _ => throw Invalid()
    };

    internal static JsonObject Participant(PcStageIntent item, string scriptId) => new()
    {
        ["participantId"] = Guid.NewGuid().ToString("D"), ["kind"] = "process", ["resourceKey"] = "automation:" + scriptId,
        ["identity"] = JsonSerializer.SerializeToNode(item, StepResultCodec.Options)
    };

    private static PcStageIntent Decode(JsonElement participant)
    {
        var json = participant.GetProperty("identity");
        var decoded = ExecutionParticipantText.Read<PcStageIntent>(json, StepResultCodec.Options, Invalid);
        var value = decoded.Value;
        if (!decoded.MatchesOriginal ||
            value.Format != Format || value.Version != 1 || value.Binding is null || value.Artifact is null ||
            value.Binding.InstanceId == Guid.Empty || value.Binding.ParentJournalId == Guid.Empty || value.Binding.ParentOperationId == Guid.Empty ||
            value.Binding.AttemptId == Guid.Empty || string.IsNullOrWhiteSpace(value.Binding.StepId) || !Enum.IsDefined(value.Outcome) || value.Outcome == TargetExecutionOutcome.UnverifiedDispatch ||
            !NexusPipeline.Core.Configuration.FileTreePolicy.Digest(value.Binding.TotalPlanSha256) ||
            (value.Outcome == TargetExecutionOutcome.StoppedByRequest
                ? value.ReasonCode is not (DispatchIntentCodec.Reason or DispatchIntentCodec.InterruptionReason or DispatchIntentCodec.BudgetReason or DispatchIntentCodec.TargetReason)
                : value.ReasonCode != Reason(value.Outcome)) || participant.GetProperty("kind").GetString() != "process") throw Invalid();
        StepEvidenceCodec.Plan(value.Artifact);
        return value;
    }

    private static IOException Invalid() => new("execution_target_stage_unrecognized");
}

internal sealed class PcStageEvidence(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
    ExecutionSqlScope? readScope = null)
{
    private readonly TargetJobEvidence _targets = new(instance, database, contracts, readScope);
    internal IReadOnlyList<BlobReference> RequireAll(AttemptJournalView journal, ExecutionPlan total, string totalSha256) =>
        ExecutionInvocationCodec.Views(journal).Select(ExecutionInvocationCodec.Current)
            .SelectMany(view => RequireCurrent(view, total, totalSha256)).Distinct().ToArray();

    private IReadOnlyList<BlobReference> RequireCurrent(AttemptJournalView journal, ExecutionPlan total, string totalSha256)
    {
        var references = new List<BlobReference>();
        foreach (var item in PcStageCodec.Read(journal, total, totalSha256))
        {
            var value = _targets.Read<PcStageDocument>(item.Artifact);
            var target = total.Steps.Single(step => step.StepId == item.Binding.StepId).Target!;
            var pc = target.Pc!;
            if (value.Format != PcStageCodec.Format + "-evidence" || value.Version != 1 || value.Binding != item.Binding ||
                value.Outcome != item.Outcome || value.ReasonCode != item.ReasonCode || value.ReadinessMode != pc.ReadinessMode ||
                value.ElapsedMilliseconds is < 0 or > 604800000 || value.ReadyMembers.IsDefault || value.ReadyMembers.Length > 4096 ||
                value.ReadyMembers.Any(member => member is null) ||
                value.ReadyMembers.DistinctBy(member => (member.ProcessId, member.CreatedAtUtc)).Count() != value.ReadyMembers.Length ||
                value.ReadyMembers.Any(member => !StringComparer.OrdinalIgnoreCase.Equals(member.ImagePath, pc.ReadyImage.Path) ||
                    member.ImageSha256 != pc.ReadyImage.Sha256)) throw Invalid();
            foreach (var member in value.ReadyMembers) TargetJobEvidence.RequireIdentity(member);
            if ((value.TargetJob is null) != (value.TargetEvidence is null)) throw Invalid();
            references.Add(item.Artifact);
            TargetJobIntent? job = null;
            if (value.TargetJob is { } binding)
            {
                if (binding.InstanceId != item.Binding.InstanceId || binding.ParentJournalId != item.Binding.ParentJournalId ||
                    binding.ParentOperationId != item.Binding.ParentOperationId || binding.AttemptId != item.Binding.AttemptId ||
                    binding.StepId != item.Binding.StepId || binding.TotalPlanSha256 != item.Binding.TotalPlanSha256) throw Invalid();
                job = TargetJobCodec.ReadAll(journal, total, totalSha256)
                    .SingleOrDefault(candidate => candidate.Binding == binding && candidate.Evidence == value.TargetEvidence &&
                        candidate.Stage is "root" or "ready" or "stopped") ?? throw Invalid();
                references.AddRange(_targets.Require(new(binding, job.Stage, job.Evidence), total, journal));
            }
            switch (value.Outcome)
            {
                case TargetExecutionOutcome.Skipped:
                    if (value.TargetJob is not null || !value.ReadyMembers.IsEmpty || target.LaunchByHost) throw Invalid();
                    break;
                case TargetExecutionOutcome.ExternalReady:
                    if (value.TargetJob is not null || value.ReadyMembers.IsEmpty) throw Invalid();
                    break;
                case TargetExecutionOutcome.OwnedReady:
                    if (job is not { Stage: "ready" } || value.ReadyMembers.IsEmpty) throw Invalid();
                    var ready = _targets.Read<TargetReadyDocument>(job.Evidence!);
                    if (!ready.Members.SequenceEqual(value.ReadyMembers)) throw Invalid();
                    break;
                case TargetExecutionOutcome.LaunchFailed:
                case TargetExecutionOutcome.ReadinessTimedOut:
                    if (job is not { Stage: "stopped" } || !value.ReadyMembers.IsEmpty) throw Invalid();
                    break;
                case TargetExecutionOutcome.Blocked:
                    if (job is not null || !value.ReadyMembers.IsEmpty) throw Invalid();
                    break;
                case TargetExecutionOutcome.ReadinessUnknown:
                    if (job is not { Stage: "stopped" } || !value.ReadyMembers.IsEmpty) throw Invalid();
                    break;
                case TargetExecutionOutcome.StoppedByRequest:
                    if (!value.ReadyMembers.IsEmpty || DispatchIntentCodec.Read(journal, total)?.ReasonCode != item.ReasonCode) throw Invalid();
                    break;
                default: throw Invalid();
            }
        }
        return references.Distinct().ToArray();
    }

    private static IOException Invalid() => new("execution_target_stage_evidence_unbound");
}

internal static class PcStageProjection
{
    internal static void RequireMainStartInside(SqliteConnection connection, SqliteTransaction transaction,
        AttemptWriterState state, StepWriterState writer)
    {
        var step = state.Total.Steps.Single(item => item.StepId == writer.StepId);
        if (writer.Role is not ("process" or "judgement") || step.Target?.Kind != "pc") return;
        var stage = PcStageCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)
            .SingleOrDefault(item => item.Binding.StepId == writer.StepId);
        if (stage?.Outcome is not (TargetExecutionOutcome.Skipped or TargetExecutionOutcome.ExternalReady or TargetExecutionOutcome.OwnedReady))
            throw new IOException("execution_target_main_start_unavailable");
        RequireConfigurationInstalledInside(connection, transaction, state, writer.StepId);
    }

    internal static void RequireInside(SqliteConnection connection, SqliteTransaction transaction,
        AttemptWriterState state, JournalDocument before, JournalDocument next)
    {
        var old = PcStageCodec.Read(before.Root, state.Total, state.Attempt.ExecutionPlan.Sha256);
        var current = PcStageCodec.Read(next.Root, state.Total, state.Attempt.ExecutionPlan.Sha256);
        foreach (var item in current.Skip(old.Length))
        {
            if (!state.IsExecutionStart || state.Gate.State != EffectGateState.Entered ||
                before.Phase != JournalPhase.Applying || next.Phase != JournalPhase.Applying) throw Invalid();
            var main = state.Jobs.Single(job => job.StepId == item.Binding.StepId && job.Role == "process");
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT NOT EXISTS(SELECT 1 FROM main.journal_writer_job_roots WHERE journal_id=$journal AND job_name=$main);";
            command.Parameters.AddWithValue("$journal", item.Binding.ParentJournalId.ToString("D"));
            command.Parameters.AddWithValue("$main", main.JobName);
            if ((long)command.ExecuteScalar()! != 1) throw Invalid();
            if (item.Outcome == TargetExecutionOutcome.StoppedByRequest)
            {
                var close = ExecutionCancellationRows.Read(connection, transaction, state.Journal, state.Attempt);
                if (close?.ReasonCode != item.ReasonCode) throw Invalid();
            }
        }
    }

    internal static void RequireConfigurationInstalledInside(SqliteConnection connection, SqliteTransaction transaction,
        AttemptWriterState state, string stepId)
    {
        var expected = ExecutionInvocationCodec.CurrentPlans(state.Latest.Root, state.Attempt, stepId).ToArray();
        var groups = new List<(Guid Id, string Subject)>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT s.session_id,o.subject_id FROM configuration.configuration_run_sessions s JOIN main.operations o ON o.operation_id=s.session_id " +
                "JOIN main.operations parent ON parent.operation_id=s.owner_operation_id AND parent.subject_id=o.subject_id " +
                "WHERE s.parent_journal_id=$journal AND s.step_id=$step AND s.invocation_ordinal=$invocation ORDER BY s.session_id LIMIT 2;";
            command.Parameters.AddWithValue("$journal", state.Journal.JournalId.ToString("D")); command.Parameters.AddWithValue("$step", stepId);
            command.Parameters.AddWithValue("$invocation", ExecutionInvocationCodec.Ordinal(state.Latest.Root, stepId));
            using var rows = command.ExecuteReader();
            while (rows.Read()) groups.Add((Guid.Parse(rows.GetString(0)), rows.GetString(1)));
        }
        if (expected.Length == 0) { if (groups.Count != 0) throw Invalid(); return; }
        if (groups.Count != 1) throw Invalid();
        var session = ConfigurationRunRows.Read(connection, transaction, groups[0].Id, groups[0].Subject) ?? throw Invalid();
        if (session.OwnerOperationId != state.Journal.OperationId || session.ParentJournalId != state.Journal.JournalId ||
            session.StepId != stepId || session.Phase != ConfigurationRunPhase.Running || session.IsWriterAdmissionClosed ||
            session.Resources.Length != expected.Length ||
            !session.Resources.Select(item => (item.PlanPath, item.PlanDigest)).ToHashSet()
                .SetEquals(expected.Select(item => (item.RelativePath, item.Sha256)))) throw Invalid();
        foreach (var resource in session.Resources)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM main.operation_journals WHERE journal_id=$journal AND operation_id=$operation " +
                "AND plan_path=$path AND plan_digest=$digest AND phase=$phase);";
            command.Parameters.AddWithValue("$journal", resource.JournalId.ToString("D"));
            command.Parameters.AddWithValue("$operation", resource.OperationId.ToString("D")); command.Parameters.AddWithValue("$path", resource.PlanPath);
            command.Parameters.AddWithValue("$digest", resource.PlanDigest); command.Parameters.AddWithValue("$phase", "applied");
            if ((long)command.ExecuteScalar()! != 1) throw Invalid();
        }
    }

    private static IOException Invalid() => new("execution_pc_target_stage_projection_unbound");
}
