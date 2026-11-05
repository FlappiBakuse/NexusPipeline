using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Storage.Configuration;

internal enum WriterPersistencePoint { RootPublished }

internal interface IWriterPersistenceObserver
{
    void Reached(WriterPersistencePoint point, ProcessIdentity root);
}
