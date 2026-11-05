using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class PcStageEvidence(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
    ExecutionSqlScope? readScope = null)
{
    private readonly TargetJobEvidence _targets = new(instance, database, contracts, readScope);
    internal IReadOnlyList<BlobReference> RequireAll(JsonElement journal, ExecutionPlan total, string totalSha256) =>
        ExecutionInvocationCodec.Views(journal).Select(ExecutionInvocationCodec.Current)
            .SelectMany(view => RequireCurrent(view, total, totalSha256)).Distinct().ToArray();

    private IReadOnlyList<BlobReference> RequireCurrent(JsonElement journal, ExecutionPlan total, string totalSha256)
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
