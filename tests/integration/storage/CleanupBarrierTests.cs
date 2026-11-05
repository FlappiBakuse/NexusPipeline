using System.Runtime.Versioning;
using NexusPipeline.Platform.Extensions;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class CleanupBarrierTests
{
    [Fact(DisplayName = "An Actual Scope Cleanup Failure Keeps Its Runtime Guard And Protected File Until The Failure Is Resolved")]
    public async Task HoldsFailedCleanup()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "cleanup-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "protected.bin"); File.WriteAllBytes(path, [1, 2, 3]);
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var scope = new ExtensionScope("fixture-plugin");
        var runtime = new GuardedRuntime(scope, held);
        await scope.OwnAsync(runtime); await scope.OwnAsync(new FailingResource());
        var failure = await Assert.ThrowsAsync<AggregateException>(async () => await scope.DisposeAsync());
        Assert.Contains(failure.InnerExceptions, item => item.Message == "fixture_native_destructor_failed");
        Assert.Contains(failure.InnerExceptions, item => item.Message == "extension_runtime_cleanup_retained");
        Assert.False(runtime.Released); Assert.False(scope.CleanupCompletedSuccessfully);
        Assert.Throws<IOException>(() => { using var denied = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); });
        await Assert.ThrowsAsync<AggregateException>(async () => await scope.DisposeAsync());
        Assert.False(runtime.Released); Assert.Equal(3, held.Length);
        var normal = new ExtensionScope("normal-plugin");
        using var ordinary = new FileStream(Path.Combine(root, "ordinary.bin"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        var successful = new GuardedRuntime(normal, ordinary);
        await normal.OwnAsync(successful); await normal.DisposeAsync();
        Assert.True(successful.Released); Assert.True(normal.CleanupCompletedSuccessfully);
        using var reopened = new FileStream(Path.Combine(root, "ordinary.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(0, reopened.Length);
    }

    private sealed class FailingResource : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => throw new IOException("fixture_native_destructor_failed");
    }

    private sealed class GuardedRuntime(ExtensionScope scope, IDisposable file) : IAsyncDisposable
    {
        internal bool Released;
        public ValueTask DisposeAsync()
        {
            RuntimeCleanupGuard.RequireRelease(scope);
            file.Dispose(); Released = true;
            return ValueTask.CompletedTask;
        }
    }
}
