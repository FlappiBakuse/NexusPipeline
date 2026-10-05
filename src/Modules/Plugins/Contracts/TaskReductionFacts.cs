namespace NexusPipeline.Modules.Plugins.Contracts;

internal sealed record TaskReductionFacts(
    TaskObservation[] Observations,
    string RunBoundary,
    TaskEvidence[] BoundaryEvidence,
    string[]? BoundaryStructuredEvidenceRefs,
    TaskIncident[]? Incidents);

internal sealed record ProviderExecutionFacts(
    string RunId,
    string AttemptId,
    TaskObservation[] Observations,
    string RunBoundary,
    string[]? BoundaryEvidenceRefs);
