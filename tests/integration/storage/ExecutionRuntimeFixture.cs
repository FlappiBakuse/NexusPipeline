using System.Diagnostics;
using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Automation;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class ExecutionRuntimeFixture : IDisposable
{
    private readonly ExecutionSequentialFixture _source;
    internal ExecutionAdmissionFixture Base => _source.Base;
    internal PlanBuilderFixture Source => Base.Source;
    internal string Marker => _source.Marker;
    internal AttemptAcceptanceResult Accepted { get; }
    internal ExecutionRuntimeConsumer Consumer { get; }
    internal AttemptWriterState State => AttemptWriterState.Read(Source.Instance, Source.Database, new(), Accepted.JournalId);

    internal ExecutionRuntimeFixture(bool configuration = false, string mode = "tail", bool expired = false,
        AutomationJudgement? judgement = null, AutomationLimits? limits = null, bool independentSuccessors = false, bool successDependency = false,
        bool captureFiles = true, Action<ExecutionAdmissionFixture>? configure = null, IAttemptTerminalObserver? terminalObserver = null)
    {
        _source = new(configuration, mode, accept: false, judgement, limits, independentSuccessors, successDependency, captureFiles);
        try
        {
            configure?.Invoke(Base);
            Consumer = new(Source.Instance, Source.Database, new(), Source.Lease, Base.Resources, Base.Index, Base.Inbox, terminalObserver);
            Accepted = Base.Service.Start(Guid.NewGuid(), ExecutionSequentialFixture.Subject, Source.Request(new QueueTarget(Source.QueueId)),
                expired ? DateTimeOffset.UtcNow.AddMinutes(-10) : DateTimeOffset.UtcNow);
        }
        catch { _source.Dispose(); throw; }
    }

    internal void Cancel()
    {
        var state = State;
        new ExecutionCancellationStore(Source.Instance, Source.Database, new(), Source.Lease).Cancel(
            ExecutionCancellationStore.Identity(Guid.NewGuid(), ExecutionSequentialFixture.Subject, Accepted.JournalId, state.Journal.OperationId,
                Guid.Parse(state.Attempt.RunId), Guid.Parse(state.Attempt.AttemptId)), Accepted.JournalId, DateTimeOffset.UtcNow);
    }

    internal async Task Ready(string? suffix = null)
    {
        var path = Marker + suffix; var timer = Stopwatch.StartNew();
        while (!File.Exists(path) && timer.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(10);
        Assert.True(File.Exists(path), path);
    }

    public void Dispose() => _source.Dispose();
}
