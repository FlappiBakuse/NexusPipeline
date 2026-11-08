namespace NexusPipeline.Host.Desktop;

internal enum StartupStage
{
    OpeningInstance,
    CheckingDatabase,
    RecoveringOperations,
    LoadingSettings,
    RestoringExecution,
    PreparingServices,
    InstallingExtensions,
    StartingExtensions,
    StartingServer,
    StartingDesktop,
    StartupUpdates,
    Ready
}
