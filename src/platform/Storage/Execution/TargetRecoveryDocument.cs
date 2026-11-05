using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record TargetRecoveryDocument(string Format, int Version, ExecutionTargetBinding Binding,
    ProcessIdentity Creator, ProcessIdentity? Root, BlobReference? Intent, TargetRecoveryObservation Observation, DateTimeOffset ObservedAt);
