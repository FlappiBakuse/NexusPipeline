namespace NexusPipeline.Platform.Storage.Execution;

internal enum TargetPersistencePoint
{
    RootAdmitted, RetentionPrepared, HandoffApplied, AndroidCollectedPublished,
    CaptureSourcePublished, CaptureRootPublished, CaptureParentStopped, CaptureCollectedAppended,
    CaptureCommandRootPublished, CaptureCommandObservedPublished
}

internal interface ITargetPersistenceObserver
{
    void Reached(TargetPersistencePoint point, ExecutionTargetBinding binding);
    void Reached(TargetPersistencePoint point, ProviderLauncherBinding binding) { }
    void Reached(TargetPersistencePoint point, ExecutionCaptureBinding binding) { }
}
