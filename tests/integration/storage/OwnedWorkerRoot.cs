using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed record OwnedWorkerRoot(int ProcessId, DateTime CreatedAtUtc)
{
    internal static OwnedWorkerRoot From(ProcessIdentity process) => new(process.ProcessId, process.CreatedAtUtc);

    internal static OwnedWorkerRoot Read(string path) =>
        Assert.IsType<OwnedWorkerRoot>(JsonSerializer.Deserialize<OwnedWorkerRoot>(File.ReadAllText(path)));

    internal ProcessIdentity RequireStopped(JobClosureStore store, Guid journalId, StepWriterState job)
    {
        var closure = store.ReadForExecutionEvidence(journalId, job.WriterId, job.JobName, job.SessionId);
        Assert.NotNull(closure);
        Assert.True(closure.WasExecuted);
        var root = Assert.IsType<ProcessIdentity>(closure.Root);
        Assert.Equal(this, From(root));
        Assert.Contains(root, closure.Family);
        Assert.All(closure.Family, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
        return root;
    }
}
