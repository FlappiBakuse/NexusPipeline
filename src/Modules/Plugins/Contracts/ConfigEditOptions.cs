namespace NexusPipeline.Modules.Plugins.Contracts;


internal sealed record ConfigEditOptions(
    bool IsolateSiblingCandidates,
    ConfigEditFreshInput? FreshInput = null);
