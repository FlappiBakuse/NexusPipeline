using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ClosurePublicationTests
{
    [Theory(DisplayName = "A Real Stopped Family Binds And Records Its Closure Only After Earlier Execution Publications Finish")]
    [InlineData("bind")]
    [InlineData("record")]
    public async Task OrdersClosurePublication(string boundary)
    {
        InstanceLease? lease = null;
        BlockingScope? scope = null;
        using var fixture = new StepWriterFixture(scopeWrapper: inner => scope = new(inner, () => lease!, boundary));
        lease = fixture.Base.Source.Lease;
        var root = await fixture.StartAsync();
        var stopping = Task.Run(() => fixture.Writer.StopAsync(TimeSpan.FromSeconds(10)));
        try
        {
            Assert.True(scope!.Entered.Wait(TimeSpan.FromSeconds(10)));
            var timer = Stopwatch.StartNew();
            while (!scope.Called && lease.ExecutionPublicationCount < 2 && timer.Elapsed < TimeSpan.FromSeconds(10))
                await Task.Delay(10);
            Assert.False(scope.Called);
            Assert.Equal(2, lease.ExecutionPublicationCount);
            Assert.False(stopping.IsCompleted);
        }
        finally { scope!.Release.Set(); }
        await scope.Publication!.WaitAsync(TimeSpan.FromSeconds(10));
        var stopped = await stopping.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(scope.Called); fixture.AssertClosed(stopped, executed: true);
        Assert.Equal(root.ProcessId, stopped.Closure.Root!.ProcessId);
        var repeated = await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10));
        fixture.AssertClosed(repeated, stopped.WasExecuted);
        Assert.Equal(stopped.Reference, repeated.Reference);
        scope.Dispose();
    }

    private sealed class BlockingScope(IConfigurationWriterScope inner, Func<InstanceLease> lease, string boundary)
        : IConfigurationWriterScope, IDisposable
    {
        internal ManualResetEventSlim Entered { get; } = new();
        internal ManualResetEventSlim Release { get; } = new();
        internal Task? Publication { get; private set; }
        internal bool Called => Volatile.Read(ref _called) != 0;
        private int _called;
        public Guid OperationId => inner.OperationId;
        public Guid ProcessScopeId => inner.ProcessScopeId;
        public ImmutableArray<Guid> JournalIds => inner.JournalIds;
        public ConfigurationEditorCommand? Command => inner.Command;
        public void RequirePrepared() => inner.RequirePrepared();
        public void RequireStart() => inner.RequireStart();
        public void RegisterJob(OwnedWorkerJob job) => inner.RegisterJob(job);
        public void RecordRoot(string jobName, ProcessIdentity root) => inner.RecordRoot(jobName, root);
        public void CloseAdmission(long elapsedMilliseconds)
        {
            inner.CloseAdmission(elapsedMilliseconds);
            if (boundary == "bind") BlockPublication();
        }
        public FrozenJobClosure BindClosure(FrozenJobClosure closure, long elapsedMilliseconds)
        {
            if (boundary == "bind") Volatile.Write(ref _called, 1);
            var result = inner.BindClosure(closure, elapsedMilliseconds);
            if (boundary == "record") BlockPublication(allowQueued: true);
            return result;
        }
        public BlobReference RecordClosed(FrozenJobClosure closure)
        {
            if (boundary == "record") Volatile.Write(ref _called, 1);
            return inner.RecordClosed(closure);
        }
        private void BlockPublication(bool allowQueued = false)
        {
            Publication = Task.Run(() => lease().PublishExecution(() =>
            {
                Entered.Set();
                if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new IOException("fixture_publication_timeout");
            }));
            var timer = Stopwatch.StartNew();
            while (!Entered.IsSet && !(allowQueued && lease().ExecutionPublicationCount >= 2) && timer.Elapsed < TimeSpan.FromSeconds(10))
                Thread.Sleep(1);
            if (!Entered.IsSet && !(allowQueued && lease().ExecutionPublicationCount >= 2))
                throw new IOException("fixture_publication_not_queued");
        }
        public void Dispose() { Entered.Dispose(); Release.Dispose(); }
    }
}
