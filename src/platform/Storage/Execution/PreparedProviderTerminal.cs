using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;

namespace NexusPipeline.Platform.Storage.Execution;

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
