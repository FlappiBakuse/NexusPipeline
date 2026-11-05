using System.Collections.Immutable;
using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Core.Extensions;

public sealed record AdapterResourceSelection(string ResourceId, long ExpectedRevision, string RelativePath);
public sealed record AdapterSelection(Guid OperationId, Guid BindingId, long ExpectedBindingRevision,
    string ExtensionId, long ExpectedInstalledRevision, ImmutableArray<AdapterResourceSelection> Resources,
    ImmutableArray<ConfigurationPatchRequest> ConfigurationIntents = default);
public sealed record AdapterResourceStamp(string ResourceId, long Revision, string RelativePath, string Sha256);
public sealed record AdapterSourceStamp(Guid BindingId, long BindingRevision, long ScriptRevision,
    string ExtensionId, long InstalledRevision, string ManifestSha256, ImmutableArray<AdapterResourceStamp> Resources)
{
    public string? CompilationFingerprint { get; init; }
}
public enum AdapterWorkflowRole { Business, Technical, Cleanup }
public sealed record AdapterTask(string TaskId, string Title, bool IsSelected, bool IsEnabled,
    ImmutableArray<string> Dependencies, bool IsRetrySafe, AdapterWorkflowRole WorkflowRole = AdapterWorkflowRole.Business)
{
    public ImmutableDictionary<string, string> TitleLocales { get; init; } = ImmutableDictionary<string, string>.Empty;
    public AdapterTaskProjection? Projection { get; init; }
    public string? ExecutionTaskId { get; init; }
    public int? RepetitionLimit { get; init; }
    public ImmutableArray<string> RetryPrerequisites { get; init; } = [];
    public ImmutableArray<string> NativeResumeScope { get; init; } = [];
}
public sealed record AdapterTaskProjection(string ResourceId, string RelativePath, ConfigurationSelector Selector);
public enum AdapterDiagnosticSeverity { Information, Warning, Blocking }
public sealed record AdapterDiagnostic(string Code, string ResourceId, AdapterDiagnosticSeverity Severity);
public enum AdapterObservationState { Succeeded, Partial, Failed, Unknown }
public enum AdapterCompletion { Completed, Satisfied, Inapplicable, NotExecuted }
public sealed record AdapterTaskObservation(string TaskId, AdapterObservationState State, string ReasonCode,
    AdapterCompletion? CompletionKind = null)
{
    public int? CompletedRepetitions { get; init; }
}
public sealed record AdapterRepetitionOffset(string TaskId, int CompletedRepetitions);
public sealed record AdapterDiscovery(AdapterSourceStamp Source, ImmutableArray<AdapterTask> Tasks,
    ImmutableArray<AdapterDiagnostic> Diagnostics)
{
    public AdapterCoverage? Coverage { get; init; }
    public string GamePathCheck { get; init; } = "notApplicable";
}
public sealed record AdapterCoverage(int Selected, int Projected, int RetrySafe, bool HasBlockingDiagnostics)
{
    public bool IsComplete => Selected > 0 && Selected == Projected && Selected == RetrySafe && !HasBlockingDiagnostics;
}
public sealed record AdapterConfigurationProposal(AdapterSourceStamp Source, ImmutableArray<ConfigurationPatchRequest> Patches,
    ImmutableArray<AdapterDiagnostic> Diagnostics, string ProposalDigest);
public sealed record AdapterRepairPreview(AdapterSourceStamp Source, int CandidateIndex, string ProposalDigest,
    string CandidateSha256, int SizeBytes, bool HasChanges);
public sealed record FrozenAdapterAttempt(Guid OperationId, Guid RunId, Guid AttemptId, string StepId, string PlanDigest);
public sealed record FrozenAdapterRetry(FrozenAdapterAttempt Attempt, string IdempotencyKey,
    ImmutableArray<AdapterTaskObservation> Observations);

public interface ITaskAdapterPort
{
    ValueTask<AdapterDiscovery> DiscoverAsync(string serverSubject, AdapterSelection selection, CancellationToken cancellationToken, Action? requireCaller = null);
    ValueTask<AdapterConfigurationProposal> ProposeConfigurationAsync(string serverSubject, AdapterSelection selection, CancellationToken cancellationToken, Action? requireCaller = null);
    ValueTask<AdapterRepairPreview> PreviewRepairAsync(string serverSubject, AdapterSelection selection,
        int candidateIndex, string expectedProposalDigest, CancellationToken cancellationToken, Action? requireCaller = null);
    ValueTask<ConfigurationSaveResult> ApplyRepairAsync(string serverSubject, AdapterSelection selection,
        int candidateIndex, string expectedProposalDigest, DateTimeOffset acceptedAt, CancellationToken cancellationToken, Action? requireCaller = null);
    ValueTask<ImmutableArray<AdapterTaskObservation>> ObserveAsync(FrozenAdapterAttempt attempt, CancellationToken cancellationToken);
    ValueTask<ImmutableArray<string>> SelectRetryAsync(FrozenAdapterRetry retry, CancellationToken cancellationToken);
}
