using System.Collections.Immutable;
using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed record ExtensionWorkNode(int Index, string RelativePath, FileObjectKind Kind, long SizeBytes,
    string? Sha256, PhysicalObjectIdentity ParentIdentity, PhysicalObjectIdentity Identity);
internal sealed record ExtensionWorkProof(string Format, int Version, Guid InstanceId, Guid ProducerOperationId,
    Guid ProducerJournalId, BlobReference ProducerPlan, long ProducerRevision, string ProducerSha256,
    BlobReference TerminalArtifact, string Layout, string WorkspacePath, PhysicalObjectIdentity ParentIdentity,
    PhysicalObjectIdentity WorkspaceIdentity, ImmutableArray<ExtensionWorkNode> Nodes);
internal sealed record ExtensionWorkPage(string Format, int Version, Guid InstanceId, Guid OperationId, Guid JournalId,
    BlobReference Proof, Guid? PreviousJournalId, BlobReference? PreviousPlan, ImmutableArray<int> Indices);
internal sealed record BoundExtensionWork(BlobReference Reference, ExtensionWorkProof Proof, BoundExtensionPayload Producer);
internal sealed record BoundWorkPage(BlobReference Reference, ExtensionWorkPage Page, BoundExtensionWork Origin);
