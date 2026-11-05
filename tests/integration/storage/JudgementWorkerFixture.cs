using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Platform.Execution.Judgement;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class JudgementWorkerFixture : IAsyncDisposable
{
    private readonly OwnedWorkerJob _job = new();
    private readonly OwnedProcessOutput _output = new(redirectInput: true);
    private readonly ProcessStartObserver _observer;
    private readonly ConcurrentDictionary<ProcessIdentity, byte> _observed = new();
    private readonly JudgementInterpreterLease _lease;
    internal JudgementWorkerInitialization Initialization { get; }
    internal JudgementWorkerClient Client { get; }
    internal OwnedWorkerIdentity Root { get; private set; } = null!;
    internal List<ProcessIdentity> AdmittedChildren { get; } = [];
    internal string DirectoryPath { get; }

    internal JudgementWorkerFixture(string language, string source, int timeoutSeconds = 3, int capacity = 4,
        Func<JudgementProbeRequest, CancellationToken, Task<JudgementProbeReply>>? observeProbe = null,
        Func<Func<CancellationToken, Task>, CancellationToken, Task>? admitInvocation = null)
    {
        DirectoryPath = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "judgement-worker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
        var primary = new StepEvidenceBinding(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), "binding-" + Guid.NewGuid().ToString("D"), "Local\\NexusWorker-" + Guid.NewGuid().ToString("N"),
            Guid.NewGuid(), 1, new string('a', 64));
        Initialization = new(JudgementWorkerProtocol.InitializationFormat, 1, new(primary, _job.Name, Guid.NewGuid()),
            language, source, JudgementWorkerProtocol.Hash(JudgementWorkerProtocol.Utf8.GetBytes(source)),
            JudgementInterpreterLease.Inspect(Environment.GetEnvironmentVariable("NEXUS_TEST_HOST_EXE")!),
            language == "python" ? JudgementInterpreterLease.Inspect(Environment.GetEnvironmentVariable("NEXUS_TEST_PYTHON_EXE")!) : null,
            DirectoryPath, PhysicalObjectIdentity.Read(DirectoryPath), timeoutSeconds, capacity);
        _lease = new(Initialization);
        _observer = _job.ObserveProcessIdentities(identity => _observed.TryAdd(identity, 0));
        Client = new(_output, Initialization, AdmitAsync, admitInvocation, observeProbe);
    }

    internal async Task StartAsync()
    {
        Root = _job.Start(Initialization.Broker.Path, ["--execution-judgement-worker"], DirectoryPath,
            Initialization.Identity.Primary.InstanceId, Initialization.Identity.Primary.AttemptId, _ => { }, _output);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Client.InitializeAsync(Root, deadline.Token);
    }

    internal JudgementWorkerInvocation Invocation(long generation, bool final = false, string input = "{}")
    {
        using var parsed = JsonDocument.Parse(input);
        var element = parsed.RootElement.Clone();
        return new(JudgementWorkerProtocol.InvocationFormat, 1, Initialization.Identity, generation, final, element,
            JudgementWorkerProtocol.Hash(JsonSerializer.SerializeToUtf8Bytes(element)));
    }

    internal async Task<JudgementWorkerExchange> InvokeAsync(long generation, bool final = false, string input = "{}")
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(Initialization.InvocationTimeoutSeconds + 10));
        return await Client.InvokeAsync(Invocation(generation, final, input), deadline.Token);
    }

    internal async Task AssertExitedAsync()
    {
        Assert.Equal(0, await _job.AwaitRootAsync(Root, TimeSpan.FromSeconds(10)));
        await Client.CompleteAsync(CancellationToken.None);
    }

    internal async Task AssertFrameAsync(JudgementWorkerInvocation invocation)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await JudgementWorkerHost.WriteAsync(_output.StandardInput, invocation, deadline.Token);
        Assert.Equal(1, await _job.AwaitRootAsync(Root, TimeSpan.FromSeconds(10), deadline.Token));
        Assert.Null(await JudgementWorkerFraming.ReadAsync(_output.StandardOutput, deadline.Token));
    }

    private Task AdmitAsync(ProcessIdentity child, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = System.Diagnostics.Process.GetProcessById(child.ProcessId);
        Assert.True(_job.ContainsProcessHandle(process.SafeHandle));
        Assert.Equal(child, ProcessIdentityReader.Read(child.ProcessId));
        Assert.True(_observer.Drain(TimeSpan.FromSeconds(5)));
        Assert.Contains(child, _observed.Keys);
        AdmittedChildren.Add(child);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            var stopped = await _job.StopAsync(TimeSpan.FromSeconds(10));
            Assert.True(stopped, JsonSerializer.Serialize(_job.LastStopObservation));
            Assert.True(_observer.Drain(TimeSpan.FromSeconds(5)));
            Assert.Empty(_observer.Failures);
            Assert.All(_observed.Keys, process => Assert.Equal(NexusPipeline.Core.Execution.WriterState.Stopped, WriterStopEvidence.ObserveProcess(process)));
            await Client.DrainAfterStopAsync();
        }
        finally { _observer.Dispose(); _job.Dispose(); _output.Dispose(); _lease.Dispose(); }
    }
}
