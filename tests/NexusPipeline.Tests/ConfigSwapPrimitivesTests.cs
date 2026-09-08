using NexusPipeline.Models;
using NexusPipeline.Services;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class ConfigSwapPrimitivesTests
{
    [Fact]
    public void CopyAs_FileAndDirectoryKeepContents()
    {
        string root = MakeTempDir();
        try
        {
            string sourceFile = Path.Combine(root, "source.json");
            string fileTarget = Path.Combine(root, "nested", "target.json");
            File.WriteAllText(sourceFile, "{\"value\":1}");

            ConfigSwapPrimitives.CopyAs(sourceFile, fileTarget, PathKind.File);

            string sourceDir = Path.Combine(root, "source-dir");
            string dirTarget = Path.Combine(root, "dir-target");
            Directory.CreateDirectory(Path.Combine(sourceDir, "child"));
            File.WriteAllText(Path.Combine(sourceDir, "child", "state.txt"), "state");
            ConfigSwapPrimitives.CopyAs(sourceDir, dirTarget, PathKind.Dir);

            Assert.Equal("{\"value\":1}", File.ReadAllText(fileTarget));
            Assert.Equal("state", File.ReadAllText(Path.Combine(dirTarget, "child", "state.txt")));
        }
        finally
        {
            DeleteExact(root);
        }
    }

    [Fact]
    public void MoveAs_RemovesSourceAfterCopy()
    {
        string root = MakeTempDir();
        try
        {
            string source = Path.Combine(root, "source");
            string target = Path.Combine(root, "target");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "state.txt"), "state");

            ConfigSwapPrimitives.MoveAs(source, target, PathKind.Dir);

            Assert.False(Directory.Exists(source));
            Assert.Equal("state", File.ReadAllText(Path.Combine(target, "state.txt")));
        }
        finally
        {
            DeleteExact(root);
        }
    }

    [Fact]
    public async Task RemoveMutex_DuringHolderAndWaiter_DefersDisposalAndPreservesSerialization()
    {
        string scriptId = "mutex-lifecycle-" + Guid.NewGuid().ToString("N");
        using var holderEntered = new ManualResetEventSlim();
        using var releaseHolder = new ManualResetEventSlim();
        using var waiterStarted = new ManualResetEventSlim();
        using var waiterEntered = new ManualResetEventSlim();
        Task? holder = null;
        Task? waiter = null;
        try
        {
            holder = Task.Run(() => ConfigSwapPrimitives.WithSwapLock(scriptId, () =>
            {
                holderEntered.Set();
                releaseHolder.Wait(TimeSpan.FromSeconds(10));
            }));
            Assert.True(holderEntered.Wait(TimeSpan.FromSeconds(10)));

            waiter = Task.Run(() =>
            {
                waiterStarted.Set();
                ConfigSwapPrimitives.WithSwapLock(scriptId, waiterEntered.Set);
            });
            Assert.True(waiterStarted.Wait(TimeSpan.FromSeconds(10)));
            Task beforeRelease = await Task.WhenAny(waiter, Task.Delay(TimeSpan.FromMilliseconds(100)));
            Assert.NotSame(waiter, beforeRelease);

            ConfigSwapPrimitives.RemoveMutex(scriptId);
            Assert.False(waiterEntered.IsSet);

            releaseHolder.Set();
            await holder.WaitAsync(TimeSpan.FromSeconds(10));
            await waiter.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(waiterEntered.IsSet);
        }
        finally
        {
            releaseHolder.Set();
            await DrainAsync(holder);
            await DrainAsync(waiter);
            ConfigSwapPrimitives.RemoveMutex(scriptId);
        }
    }

    [Fact]
    public async Task RemoveMutex_AndImmediateReopen_StillSerializeSameNamedMutex()
    {
        string scriptId = "mutex-reopen-" + Guid.NewGuid().ToString("N");
        using var holderEntered = new ManualResetEventSlim();
        using var releaseHolder = new ManualResetEventSlim();
        using var reopenedEntered = new ManualResetEventSlim();
        Task? holder = null;
        Task? reopened = null;
        try
        {
            holder = Task.Run(() => ConfigSwapPrimitives.WithSwapLock(scriptId, () =>
            {
                holderEntered.Set();
                releaseHolder.Wait(TimeSpan.FromSeconds(10));
            }));
            Assert.True(holderEntered.Wait(TimeSpan.FromSeconds(10)));

            ConfigSwapPrimitives.RemoveMutex(scriptId);
            reopened = Task.Run(() => ConfigSwapPrimitives.WithSwapLock(scriptId, reopenedEntered.Set));
            Task completed = await Task.WhenAny(reopened, Task.Delay(TimeSpan.FromMilliseconds(100)));
            Assert.NotSame(reopened, completed);
            Assert.False(reopenedEntered.IsSet);

            releaseHolder.Set();
            await holder.WaitAsync(TimeSpan.FromSeconds(10));
            await reopened.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(reopenedEntered.IsSet);
        }
        finally
        {
            releaseHolder.Set();
            await DrainAsync(holder);
            await DrainAsync(reopened);
            ConfigSwapPrimitives.RemoveMutex(scriptId);
        }
    }

    [Fact]
    public async Task SharedMutex_ConcurrentCriticalSectionsNeverOverlap()
    {
        string scriptId = "mutex-serial-" + Guid.NewGuid().ToString("N");
        int active = 0;
        int maximum = 0;
        try
        {
            Task[] workers = Enumerable.Range(0, 100)
                .Select(_ => Task.Run(() => ConfigSwapPrimitives.WithSwapLock(scriptId, () =>
                {
                    int current = Interlocked.Increment(ref active);
                    int observed;
                    do
                    {
                        observed = Volatile.Read(ref maximum);
                        if (current <= observed)
                        {
                            break;
                        }
                    }
                    while (Interlocked.CompareExchange(ref maximum, current, observed) != observed);
                    Thread.SpinWait(10_000);
                    Interlocked.Decrement(ref active);
                 })))
                 .ToArray();
            await Task.WhenAll(workers);
            Assert.Equal(1, maximum);
        }
        finally
        {
            ConfigSwapPrimitives.RemoveMutex(scriptId);
        }
    }

    [Fact]
    public void SharedMutex_AbandonedOwnerIsRecoveredByNextLease()
    {
        string scriptId = "mutex-abandoned-" + Guid.NewGuid().ToString("N");
        string name = "NexusPipeline.ConfigSwap." + scriptId;
        using var external = new Mutex(false, name);
        bool ownerAcquired = false;
        var owner = new Thread(() => ownerAcquired = external.WaitOne(TimeSpan.FromSeconds(10)))
        {
            IsBackground = true,
        };
        try
        {
            owner.Start();
            Assert.True(owner.Join(TimeSpan.FromSeconds(10)));
            Assert.True(ownerAcquired);

            bool entered = false;
            ConfigSwapPrimitives.WithSwapLock(scriptId, () => entered = true);

            Assert.True(entered);
        }
        finally
        {
            ConfigSwapPrimitives.RemoveMutex(scriptId);
        }
    }

    private static async Task DrainAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch
        {
        }
    }

    private static string MakeTempDir()
    {
        string root = Path.Combine(Path.GetTempPath(), "np-config-swap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteExact(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
