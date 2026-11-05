using System.Collections.Immutable;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record ProviderLauncherBinding(Guid InstanceId, Guid ParentJournalId, Guid ParentOperationId,
    Guid AttemptId, string StepId, Guid LauncherId, string JobName, Guid OwnerWriterId, int SessionId,
    string TotalPlanSha256, Guid ActivationId, string ExtensionId, string EntryId);

internal sealed record ProviderLauncherIntent(string Format, int Version, ProviderLauncherBinding Binding,
    string Stage, BlobReference? Artifact, EngineOutcome? LaunchEngine = null);

internal sealed record ProviderLauncherRoot(string Format, int Version,
    ProviderLauncherBinding Binding, ProcessIdentity Root);

internal sealed record ProviderLauncherStopped(string Format, int Version,
    ProviderLauncherBinding Binding, ProcessIdentity? Root, OwnedTargetInventory Inventory, bool StartReturned, int? NativeStartErrorCode);

internal sealed record ProviderLauncherRetention(string Format, int Version, ProviderLauncherBinding Binding,
    ProcessIdentity Root, OwnedTargetInventory Inventory, ImmutableArray<BlobReference> NecessaryClosures,
    BlobReference PrimaryResult, BlobReference Terminal, ImmutableArray<ResourceClaim> References, long BeforeRevision);

internal sealed record ProviderLauncherRetained(string Format, int Version, ProviderLauncherBinding Binding,
    BlobReference Intent, OwnedTargetHandoff Native);

internal sealed record ProviderLauncherEnded(string Format, int Version, ProviderLauncherBinding Binding,
    BlobReference Retained, OwnedTargetInventory Inventory);

internal sealed record ProviderLauncherState(ProviderLauncherBinding Binding, string Stage, BlobReference? Artifact, EngineOutcome? LaunchEngine);

internal sealed record ProviderLauncherStarted(string Format, int Version,
    ProviderLauncherBinding Binding, ProcessIdentity Root);

internal sealed record ProviderLauncherRecovered(string Format, int Version, ProviderLauncherBinding Binding,
    ProcessIdentity Creator, ProcessIdentity? Root, BlobReference? Intent, TargetRecoveryObservation Observation, long BeforeRevision);
