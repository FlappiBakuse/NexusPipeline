using System.Collections.Immutable;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Configuration;

internal interface IConfigurationWriterScope
{
    Guid OperationId { get; }
    Guid ProcessScopeId { get; }
    ImmutableArray<Guid> JournalIds { get; }
    ConfigurationEditorCommand? Command { get; }
    CommandLineSyntax CommandLineSyntax => CommandLineSyntax.ArgumentVector;
    void RequirePrepared();
    void RequireStart();
    void RegisterJob(OwnedWorkerJob job);
    void RecordRoot(string jobName, ProcessIdentity root);
    void CloseAdmission(long elapsedMilliseconds);
    FrozenJobClosure BindClosure(FrozenJobClosure closure, long elapsedMilliseconds);
    BlobReference RecordClosed(FrozenJobClosure closure);
    void RequirePreviousWriter(RunWriterProcess previous, StoppedConfigurationEditor stopped) =>
        throw new IOException("configuration_writer_continuation_unavailable");
}
