using System.Collections.Immutable;
using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.StorageTests;

internal sealed class CancellationFailure(ExecutionCancellationPoint point) : IExecutionCancellationObserver
{
    public void Reached(ExecutionCancellationPoint reached)
    {
        if (reached == point) throw new IOException("fixture_cancel_boundary");
    }
}

[SupportedOSPlatform("windows")]
internal sealed class CancellationRootPause(StepWriterScope inner, Func<StepWriterFixture> fixture, bool rootWins)
    : IConfigurationWriterScope, IDisposable
{
    internal ManualResetEventSlim Reached { get; } = new();
    internal ManualResetEventSlim Release { get; } = new();
    public Guid OperationId => inner.OperationId;
    public Guid ProcessScopeId => inner.ProcessScopeId;
    public ImmutableArray<Guid> JournalIds => inner.JournalIds;
    public ConfigurationEditorCommand Command => inner.Command;
    public void RequirePrepared() => inner.RequirePrepared();
    public void RequireStart() => inner.RequireStart();
    public void RegisterJob(OwnedWorkerJob job) => inner.RegisterJob(job);
    public void RecordRoot(string jobName, ProcessIdentity root)
    {
        var cached = inner.VerifyReadOnly();
        if (rootWins) inner.RecordRoot(jobName, root);
        Reached.Set();
        if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new IOException("fixture_root_boundary_timeout");
        if (!rootWins)
        {
            var owner = fixture(); var source = owner.Base.Source;
            new JobClosureStore(source.Instance, source.Database, new()).RecordAttemptRoot(cached,
                cached.Steps.Single(step => step.JobName == jobName), root);
        }
    }
    public void CloseAdmission(long elapsedMilliseconds) => inner.CloseAdmission(elapsedMilliseconds);
    public FrozenJobClosure BindClosure(FrozenJobClosure closure, long elapsedMilliseconds) => inner.BindClosure(closure, elapsedMilliseconds);
    public BlobReference RecordClosed(FrozenJobClosure closure) => inner.RecordClosed(closure);
    public void Dispose() { Release.Set(); Reached.Dispose(); Release.Dispose(); }
}
