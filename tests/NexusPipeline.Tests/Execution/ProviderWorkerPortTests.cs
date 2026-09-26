using System.Text.Json.Nodes;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.Execution.Judgement;
using NexusPipeline.Plugin.Abstractions;
using Xunit;

namespace NexusPipeline.Tests.Execution;

public sealed class ProviderWorkerPortTests
{
    [Fact]
    public async Task AuthenticatedWorkerFeedsExistingReducerAndIndependentCleanup()
    {
        using var fixture = new Fixture();
        var lines = new List<string>();
        var port = new ProviderWorkerPort(fixture.Root, fixture.Root, "execution", "record", 1, (line, _) => { lock (lines) lines.Add(line); });
        var plan = new PluginProviderPlan("plan", "revision", "authorization", [], [new("task", "Task", 0)], new());
        var projection = new ProviderTaskProjection(plan, "dummy", "1", "record", "user", "script", 1);
        PluginProviderWorkerResult result;
        try { result = await port.RunAsync(new("NexusPipeline.TestProviderWorker.exe", [], fixture.Root, new()),
            item => { projection.Accept(item); return ValueTask.CompletedTask; }, CancellationToken.None); }
        catch (Exception ex) { throw new Exception(string.Join("\n", lines), ex); }
        Assert.True(result.ExitKind == "completed", "worker=" + result.ExitKind + "\n" + string.Join("\n", lines)); Assert.Equal(0, result.ExitCode); Assert.True(result.CleanupConfirmed);
        projection.Finish("succeeded", false);
        Assert.Equal("unverified", projection.Result("done").Status);
        Assert.Equal("unknown", projection.Snapshot()["finalTaskResults"]![0]!["status"]!.GetValue<string>());
        Assert.Equal("succeeded", projection.Snapshot()["finalTaskResults"]![0]!["engineStatus"]!.GetValue<string>());
    }

    [Fact]
    public async Task WrongNonceCannotPublishFactsAndWorkerIsStopped()
    {
        using var fixture = new Fixture();
        var port = new ProviderWorkerPort(fixture.Root, fixture.Root, "execution", "record", 1, null);
        int events = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => port.RunAsync(new("NexusPipeline.TestProviderWorker.exe", [], fixture.Root,
            new() { ["wrongNonce"] = true }), _ => { events++; return ValueTask.CompletedTask; }, CancellationToken.None));
        Assert.Equal(0, events); Assert.True(port.CleanupConfirmed); Assert.False(port.TerminalReceived);
    }

    [Fact]
    public async Task CancelUsesSeparateControlChannelAndConfirmsCleanup()
    {
        using var fixture = new Fixture();
        var port = new ProviderWorkerPort(fixture.Root, fixture.Root, "execution", "record", 1, null);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var result = await port.RunAsync(new("NexusPipeline.TestProviderWorker.exe", [], fixture.Root,
            new() { ["waitForCancel"] = true }), _ => ValueTask.CompletedTask, cancel.Token);
        Assert.Equal("cancelled", result.ExitKind); Assert.True(result.CleanupConfirmed);
    }

    [Theory]
    [InlineData("../outside.exe")]
    [InlineData("C:\\Windows\\System32\\cmd.exe")]
    [InlineData("worker.cmd")]
    public void ArbitraryExecutableRequestsAreRejected(string relative)
    {
        using var fixture = new Fixture();
        Assert.Throws<InvalidDataException>(() => ProviderWorkerPort.ScopedPath(fixture.Root, relative, true));
    }

    [Fact]
    public async Task HandshakeWithoutNativeReadinessCannotWaitForTheWholeRunBudget()
    {
        using var fixture = new Fixture();
        var codes = new List<string>();
        var port = new ProviderWorkerPort(fixture.Root, fixture.Root, "execution", "record", 1,
            (line, _) => codes.Add(line), TimeSpan.FromSeconds(2));
        var result = await port.RunAsync(new("NexusPipeline.TestProviderWorker.exe", [], fixture.Root,
            new() { ["neverReady"] = true }), _ => ValueTask.CompletedTask, CancellationToken.None);
        Assert.Equal("fault", result.ExitKind);
        Assert.True(result.CleanupConfirmed);
        Assert.False(port.TerminalReceived);
        Assert.Contains("provider.worker_ready_timeout", codes);
    }

    [Fact]
    public async Task OversizedUtf8BootstrapIsRejectedBeforeAnyWorkerStarts()
    {
        using var fixture = new Fixture();
        var port = new ProviderWorkerPort(fixture.Root, fixture.Root, "execution", "record", 1, null);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => port.RunAsync(
            new("NexusPipeline.TestProviderWorker.exe", [], fixture.Root, new() { ["payload"] = new string('中', 400000) }),
            _ => throw new Exception("must not publish"), CancellationToken.None));
        Assert.Equal("provider.bootstrap_size", exception.Message);
        Assert.True(port.CleanupConfirmed);
        Assert.False(port.TerminalReceived);
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly bool _keepEvidence;
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "nxp-provider-worker-" + Guid.NewGuid().ToString("N"));
        internal Fixture(bool keepEvidence = false)
        {
            _keepEvidence = keepEvidence;
            Directory.CreateDirectory(Root);
            string repository = FindRoot();
            string configuration = AppContext.BaseDirectory.Split(Path.DirectorySeparatorChar)
                .Last(part => part is "Debug" or "Release");
            bool testHost = AppContext.BaseDirectory.StartsWith(Path.Combine(repository, "bin", "test-host") + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
            string source = testHost
                ? Path.Combine(repository, "bin", "test-host", "NexusPipeline.TestProviderWorker", configuration, "net8.0-windows")
                : Path.Combine(repository, "tests", "fixtures", "NexusPipeline.TestProviderWorker", "bin", configuration, "net8.0-windows");
            foreach (string file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(Root, Path.GetFileName(file)));
        }
        private static string FindRoot()
        {
            for (string? path = AppContext.BaseDirectory; path is not null; path = Path.GetDirectoryName(path))
                if (File.Exists(Path.Combine(path, "src", "NexusPipeline.csproj"))) return path;
            throw new InvalidOperationException("test repository not found");
        }
        public void Dispose() { if (!_keepEvidence) Directory.Delete(Root, true); }
    }
}
