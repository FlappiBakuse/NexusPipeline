using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Execution;
using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class ProviderControlInputs
{
    internal static ExecutionProviderBootstrap Build(ExecutionPlanStep step, ExecutionProviderSession identity, long attemptOrdinal) => new()
    {
        Format = "nexus.execution.provider-bootstrap", Version = 1, AttemptOrdinal = attemptOrdinal,
        Purpose = step.Purpose,
        IdempotencyKey = RetryAuthorityPolicy.BindingKey(identity.RunId, step.StepId), Identity = ProviderControlReader.WireIdentity(identity),
        PipeName = "NexusExecutionProvider-" + identity.Nonce, Upstream = (step.Provider ?? throw Invalid()).Upstream,
        UpstreamArguments = step.Provider.UpstreamArguments.ToArray(), WorkingDirectory = step.WorkingDirectory,
        ConfigurationTargets = step.ConfigurationTargets.ToArray(), MaximumEvents = ProviderProtocolState.MaximumEvents,
        Device = step.Purpose == "initialization" || step.Provider.TargetControl != true ? null : new() {
            Kind = step.Target?.Kind ?? throw Invalid(), PipeName = "NexusExecutionDevice-" + identity.Nonce },
        Project = step.Adapter?.Compiled is not { } compiled ? null : new()
        { Fingerprint = compiled.Sha256, PipeName = "NexusExecutionProject-" + identity.Nonce }
    };

    internal static void RequireFits(ExecutionPlanStep step)
    {
        if (step.Provider is null) return;
        var placeholder = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var identity = new ExecutionProviderSession(placeholder, placeholder, placeholder, placeholder, placeholder,
            step.StepId, step.Provider.ExtensionId, step.Provider.EntryId, placeholder, new string('f', 64), new string('f', 64));
        _ = ExecutionProviderFrames.Encode(Build(step, identity, 99), ExecutionProviderFrames.MaximumBootstrapBytes);
    }

    internal static long Ordinal(NexusPipeline.Platform.Storage.RuntimeInstance instance,
        NexusPipeline.Platform.Storage.Sqlite.StateDatabase database, NexusPipeline.Platform.Serialization.ContractReader contracts,
        StepEvidenceBinding binding)
    {
        var parent = new NexusPipeline.Platform.Storage.Sqlite.RecoveryJournalRegistry(database).ReadJournal(binding.ParentJournalId);
        var attempt = AttemptPreparedBinding.ReadPlan(instance, contracts, parent);
        if (attempt.AttemptId != binding.AttemptId.ToString("D") || attempt.RunId != binding.RunId.ToString("D") ||
            attempt.OperationId != binding.ParentOperationId.ToString("D")) throw Invalid();
        return attempt.AttemptOrdinal;
    }

    private static IOException Invalid() => new("execution_provider_bootstrap_unbound");
}
