using NexusPipeline.Core.Maintenance;

namespace NexusPipeline.Platform.Storage.Notifications;

internal interface IWorkQueueAcceptance
{
    IPreparedWorkQueue Prepare(AcceptedExecutionRun actualOrigin);
}
internal interface IPreparedWorkQueue : IDisposable
{
    void Committed(BlobReference source);
    void CommittedManual(BlobReference source);
}
