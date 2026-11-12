using System.Collections.Concurrent;
using Xunit;

namespace NexusPipeline.StorageTests;

internal sealed class PreviewWriteWatch : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly ConcurrentQueue<string> _writes = new();
    private readonly TaskCompletionSource _observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _fence = ".preview-watch-" + Guid.NewGuid().ToString("N");

    internal PreviewWriteWatch(string runtime)
    {
        var directory = Directory.CreateDirectory(Path.Combine(runtime, "blobs"));
        _watcher = new(directory.FullName) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite, IncludeSubdirectories = true };
        _watcher.Created += (_, entry) => Observe(entry);
        _watcher.Renamed += (_, entry) => Observe(entry);
        _watcher.Changed += (_, entry) => Observe(entry);
        _watcher.Error += (_, error) => _observed.TrySetException(error.GetException());
        _watcher.EnableRaisingEvents = true;
    }

    private void Observe(FileSystemEventArgs entry)
    {
        if (entry.Name == _fence) { _observed.TrySetResult(); return; }
        if (Path.GetFileName(entry.FullPath).StartsWith(".capture-", StringComparison.Ordinal) ||
            entry.FullPath.EndsWith(".bin", StringComparison.Ordinal) || entry.FullPath.EndsWith(".json", StringComparison.Ordinal) ||
            entry.FullPath.EndsWith(".staging", StringComparison.Ordinal))
            _writes.Enqueue(entry.FullPath);
    }

    internal async Task RequireNoBlobsAsync()
    {
        var fence = Path.Combine(_watcher.Path, _fence);
        try
        {
            File.WriteAllBytes(fence, []);
            await _observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(_writes);
        }
        finally { File.Delete(fence); }
    }

    internal async Task RequireNoPngAsync()
    {
        var fence = Path.Combine(_watcher.Path, _fence);
        try
        {
            File.WriteAllBytes(fence, []);
            await _observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.DoesNotContain(_writes, path => Path.GetFileName(path).StartsWith(".capture-", StringComparison.Ordinal));
            Assert.DoesNotContain(_writes, path => File.Exists(path) && IsPng(path));
        }
        finally { File.Delete(fence); }
    }

    private static bool IsPng(string path)
    {
        using var input = File.OpenRead(path);
        Span<byte> signature = stackalloc byte[8];
        return input.ReadAtLeast(signature, signature.Length, false) == signature.Length &&
            signature.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
    }

    public void Dispose() => _watcher.Dispose();
}
