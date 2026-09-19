using System.Diagnostics;
using NexusPipeline.Plugin.Abstractions;
using Xunit;
using NexusPipeline.ControlPlane.Http;
using NexusPipeline.Modules.Execution.Targets;
using NexusPipeline.Modules.Plugins.Contracts;

namespace NexusPipeline.Tests.Execution;

public sealed class EmulatorPluginSupportTests
{
    [Fact]
    public async Task Probe_UsesOneTotalDeadlineAcrossProviders()
    {
        var secondProviderStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondProviderCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timer = Stopwatch.StartNew();
        var first = new ProbeProvider("first", async (_, _) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(650)).ConfigureAwait(false);
            return PluginEmulatorProbeResult.NotApplicable();
        });
        var second = new ProbeProvider("second", async (token, _) =>
        {
            secondProviderStarted.TrySetResult(true);
            using CancellationTokenRegistration registration = token.Register(() =>
            {
                secondProviderCancelled.TrySetResult(true);
            });
            await secondProviderCancelled.Task.ConfigureAwait(false);
            return PluginEmulatorProbeResult.NotApplicable();
        });
        var providers = new[]
        {
            Descriptor(first),
            Descriptor(second),
        };

        PluginEmulatorProbeDecision result = await PluginEmulatorProbeService.ProbeAsync(
            providers,
            "127.0.0.1:5555",
            CancellationToken.None,
            timeoutSeconds: 1);

        Assert.Equal(PluginEmulatorProbeState.Error, result.State);
        Assert.True(secondProviderStarted.Task.IsCompleted);
        Assert.True(secondProviderCancelled.Task.IsCompleted);
        Assert.True(timer.Elapsed < TimeSpan.FromMilliseconds(1500));
    }

    [Fact]
    public async Task DriverScreenshotTimeout_ReturnsWithoutWaitingForUncooperativePlugin()
    {
        var pending = new TaskCompletionSource<PluginEmulatorBinaryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var driver = new FakeDriver { CaptureTask = pending.Task };
        var adapter = new PluginEmulatorDriverAdapter(driver, "Fixture Emulator");

        EmulatorBinaryResult result = await adapter.CaptureScreenAsync(CancellationToken.None, timeoutSeconds: 1);

        Assert.False(result.Ok);
        Assert.Contains("超时", result.Error);
        pending.TrySetResult(PluginEmulatorBinaryResult.Failure("late completion"));
    }

    [Fact]
    public async Task DriverCleanupTimeoutIsBoundedAndExternalCancellationStillPropagates()
    {
        var pendingShutdown = new TaskCompletionSource<PluginEmulatorCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var driver = new FakeDriver { ShutdownTask = pendingShutdown.Task };
        var adapter = new PluginEmulatorDriverAdapter(driver, "Fixture Emulator");

        EmulatorCommandResult shutdown = await adapter.ShutdownAsync(CancellationToken.None, timeoutSeconds: 1);

        Assert.False(shutdown.Ok);
        Assert.Contains("Shutdown", shutdown.Output);
        Assert.Contains("超时", shutdown.Output);
        pendingShutdown.TrySetResult(PluginEmulatorCommandResult.Success());

        var pendingStop = new TaskCompletionSource<PluginEmulatorCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        driver.StopTask = pendingStop.Task;
        using var cancellation = new CancellationTokenSource();
        Task<EmulatorCommandResult> stopped = adapter.StopAppAsync("com.example.game", cancellation.Token, timeoutSeconds: 30);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopped);
        pendingStop.TrySetResult(PluginEmulatorCommandResult.Success());
    }

    [Fact]
    public async Task Probe_ContainsThrowingDisplayNameAndFailsClosed()
    {
        var provider = new ProbeProvider("throwing-name", (_, _) =>
            ValueTask.FromResult(PluginEmulatorProbeResult.Match(new FakeDriver { ThrowDisplayName = true })));

        PluginEmulatorProbeDecision result = await PluginEmulatorProbeService.ProbeAsync(
            new[] { Descriptor(provider) },
            "127.0.0.1:5555",
            CancellationToken.None,
            timeoutSeconds: 2);

        Assert.Equal(PluginEmulatorProbeState.Error, result.State);
        Assert.Contains("驱动名称读取失败", result.Error);
    }

    [Fact]
    public async Task ProbeBoundsProviderThatBlocksBeforeReturningItsTask()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ProbeProvider("blocking-provider", (_, _) =>
        {
            started.TrySetResult(true);
            Thread.Sleep(TimeSpan.FromSeconds(3));
            return ValueTask.FromResult(PluginEmulatorProbeResult.NotApplicable());
        });
        var timer = Stopwatch.StartNew();

        PluginEmulatorProbeDecision result = await PluginEmulatorProbeService.ProbeAsync(
            new[] { Descriptor(provider) },
            "127.0.0.1:5555",
            CancellationToken.None,
            timeoutSeconds: 1);

        Assert.Equal(PluginEmulatorProbeState.Error, result.State);
        Assert.True(started.Task.IsCompleted);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task DriverBoundsOperationThatBlocksBeforeReturningItsTask()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var driver = new FakeDriver
        {
            CaptureFactory = (_, _) =>
            {
                started.TrySetResult(true);
                Thread.Sleep(TimeSpan.FromSeconds(3));
                return Task.FromResult(PluginEmulatorBinaryResult.Failure("late result"));
            },
        };
        var adapter = new PluginEmulatorDriverAdapter(driver, "Fixture Emulator");
        var timer = Stopwatch.StartNew();

        EmulatorBinaryResult result = await adapter.CaptureScreenAsync(CancellationToken.None, timeoutSeconds: 1);

        Assert.False(result.Ok);
        Assert.Contains("超时", result.Error);
        Assert.True(started.Task.IsCompleted);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2));
    }

    private static EmulatorSupportProviderDescriptor Descriptor(IPluginEmulatorSupportProvider provider) =>
        new("fixture-emulator", provider.Id, provider.Priority, provider);

    private sealed class ProbeProvider(
        string id,
        Func<CancellationToken, int, ValueTask<PluginEmulatorProbeResult>> probe) : IPluginEmulatorSupportProvider
    {
        public string Id { get; } = id;

        public int Priority => 0;

        public ValueTask<PluginEmulatorProbeResult> ProbeAsync(string adbEndpoint, CancellationToken cancellationToken, int timeoutSeconds) =>
            probe(cancellationToken, timeoutSeconds);
    }

    private sealed class FakeDriver : IPluginEmulatorDriver
    {
        public bool ThrowDisplayName { get; init; }

        public Task<PluginEmulatorBinaryResult>? CaptureTask { get; init; }

        public Func<CancellationToken, int, Task<PluginEmulatorBinaryResult>>? CaptureFactory { get; init; }

        public Task<PluginEmulatorCommandResult>? ShutdownTask { get; init; }

        public Task<PluginEmulatorCommandResult>? StopTask { get; set; }

        public string DisplayName => ThrowDisplayName
            ? throw new InvalidOperationException("fixture display getter")
            : "Fixture Emulator";

        public Task<PluginEmulatorCommandResult> EnsureReadyAsync(CancellationToken cancellationToken, int timeoutSeconds) =>
            Task.FromResult(PluginEmulatorCommandResult.Success());

        public Task<PluginEmulatorCommandResult> StartAppAsync(IReadOnlyList<string> startArgs, CancellationToken cancellationToken, int timeoutSeconds) =>
            Task.FromResult(PluginEmulatorCommandResult.Success());

        public Task<string?> GetForegroundPackageAsync(CancellationToken cancellationToken, int timeoutSeconds) =>
            Task.FromResult<string?>(null);

        public Task<PluginEmulatorBinaryResult> CaptureScreenAsync(CancellationToken cancellationToken, int timeoutSeconds) =>
            CaptureFactory?.Invoke(cancellationToken, timeoutSeconds)
            ?? CaptureTask
            ?? Task.FromResult(PluginEmulatorBinaryResult.Failure("capture not configured"));

        public Task<PluginEmulatorCommandResult> StopAppAsync(string? packageName, CancellationToken cancellationToken, int timeoutSeconds) =>
            StopTask ?? Task.FromResult(PluginEmulatorCommandResult.Success());

        public Task<PluginEmulatorCommandResult> ShutdownAsync(CancellationToken cancellationToken, int timeoutSeconds) =>
            ShutdownTask ?? Task.FromResult(PluginEmulatorCommandResult.Success());
    }
}
