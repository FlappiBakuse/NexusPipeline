using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class BindingOutputBudget
{
    internal static long MaximumForStep(ExecutionPlanStep step) => step.Target?.Kind == "android"
        ? AndroidControlCapacity.MaximumBindingRawBytes : RawOutputCodec.MaximumRecordedBytes;

    internal static long Remaining(RuntimeInstance instance, StateDatabase database, ContractReader contracts, StepEvidenceBinding binding)
    {
        var registered = new RecoveryJournalRegistry(database).ReadJournal(binding.ParentJournalId);
        var attempt = AttemptPreparedBinding.ReadPlan(instance, contracts, registered);
        var total = new ExecutionPlanCodec(instance, contracts, new(instance, contracts))
            .Read(AttemptPlanCodec.Reference(attempt.ExecutionPlan)).ReadWire();
        return MaximumForStep(total.Steps.Single(step => step.StepId == binding.StepId)) -
            Used(instance, database, contracts, attempt, total, binding.StepId);
    }

    internal static void RequireReplacement(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        AttemptWriterState state, StepEvidenceBinding binding, string role, BlobReference artifact)
    {
        var input = state.Total.Steps.Single(step => step.StepId == binding.StepId);
        if (input.Hooks is not { Count: > 0 } && input.Target?.Kind != "android") return;
        var blobs = new ImmutableBlobStore(instance, contracts);
        BlobReference? previousRaw;
        BlobReference? nextRaw;
        if (ExecutionBindingRoles.IsHook(role))
        {
            var prior = HookResultCodec.Read(state.Latest.Root, state.Total).SingleOrDefault(item => item.StepId == binding.StepId && item.Role == role);
            previousRaw = prior is null ? null : new HookEvidenceReader(instance, database, contracts).Read(binding, role, prior.Artifact, false).RawOutput;
            nextRaw = new HookEvidenceReader(instance, database, contracts).Read(binding, role, artifact, false).RawOutput;
        }
        else
        {
            var prior = StepResultCodec.Read(state.Latest.Root, state.Total).SingleOrDefault(item => item.Binding.StepId == binding.StepId);
            previousRaw = prior is null ? null : StepEvidenceCodec.Read(blobs, binding, prior.Latest.Artifact).RawOutput;
            nextRaw = StepEvidenceCodec.Read(blobs, binding, artifact).RawOutput;
        }
        var total = Used(instance, database, contracts, binding.ParentJournalId, binding.StepId) - Count(previousRaw) + Count(nextRaw);
        if (total > MaximumForStep(input)) throw new IOException("execution_binding_output_limit");

        long Count(BlobReference? raw) => raw is null ? 0 : RawOutputCodec.Read(blobs, binding, raw).Parts.Sum(part => part.Content.SizeBytes);
    }

    internal static long Used(RuntimeInstance instance, StateDatabase database, ContractReader contracts, Guid parentJournalId, string stepId)
    {
        var registered = new RecoveryJournalRegistry(database).ReadJournal(parentJournalId);
        var attempt = AttemptPreparedBinding.ReadPlan(instance, contracts, registered);
        var total = new ExecutionPlanCodec(instance, contracts, new(instance, contracts)).Read(AttemptPlanCodec.Reference(attempt.ExecutionPlan)).ReadWire();
        return Used(instance, database, contracts, attempt, total, stepId);
    }

    internal static long Used(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        RunAttemptPlan attempt, ExecutionPlan total, string stepId)
    {
        var parentJournalId = Guid.Parse(attempt.JournalId);
        var document = new JournalStore(instance, contracts).Read(parentJournalId);
        return UsedCore(instance, database, contracts, attempt, total, document, null, stepId);
    }

    internal static long UsedFromValidatedRead(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        RegisteredJournal registered, RunAttemptPlan attempt, ExecutionPlan total, JournalDocument latest, string stepId,
        AndroidControlEvidence? android = null) =>
        UsedCore(instance, database, contracts, attempt, total, latest, registered, stepId, android);

    private static long UsedCore(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        RunAttemptPlan attempt, ExecutionPlan total, JournalDocument document, RegisteredJournal? registered, string stepId,
        AndroidControlEvidence? android = null)
    {
        var blobs = new ImmutableBlobStore(instance, contracts);
        var invocationViews = ExecutionInvocationCodec.Views(document.Root).GroupBy(view => ExecutionInvocationCodec.Ordinal(view, stepId)).Select(group => group.Last());
        long used = invocationViews.Sum(view => (android ?? new AndroidControlEvidence(instance, database, contracts))
            .OutputBytes(view, total, attempt.ExecutionPlan.Sha256, stepId));
        foreach (var hook in HookResultCodec.ReadHistory(document.Root, total).Where(item => item.StepId == stepId))
        {
            var reader = new HookEvidenceReader(instance, database, contracts);
            var value = registered is null ? reader.Read(hook.Binding, hook.Role, hook.Artifact, verifyContents: false) :
                reader.ReadFromValidatedContext(hook.Binding, hook.Role, hook.Artifact,
                    new StepEvidenceReader(instance, database, contracts).ContextFromValidatedRead(
                        hook.Binding, registered, attempt, total, document, hook.Role), verifyContents: false);
            if (value.RawOutput is { } raw) used += Count(hook.Binding, raw);
        }
        foreach (var main in ExecutionInvocationCodec.Views(document.Root).SelectMany(view => StepResultCodec.Read(view, total))
            .Where(item => item.Binding.StepId == stepId).GroupBy(item => item.Binding.JobName).Select(group => group.Last()))
        {
            var value = StepEvidenceCodec.Read(blobs, main.Binding, main.Latest.Artifact);
            if (value.RawOutput is { } raw) used += Count(main.Binding, raw);
        }
        if (used > MaximumForStep(total.Steps.Single(step => step.StepId == stepId)))
            throw new IOException("execution_binding_output_limit");
        return used;

        long Count(StepEvidenceBinding binding, BlobReference raw) =>
            RawOutputCodec.Read(blobs, binding, raw).Parts.Sum(part => part.Content.SizeBytes);
    }
}
