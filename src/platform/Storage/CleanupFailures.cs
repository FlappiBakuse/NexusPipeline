using System.Runtime.ExceptionServices;

namespace NexusPipeline.Platform.Storage;

internal sealed class CleanupFailures
{
    private static readonly object Key = new();
    private readonly List<Exception> _failures = [];

    internal void Attempt(Action cleanup)
    {
        try { cleanup(); }
        catch (Exception error) { Add(error); }
    }

    internal void Add(Exception error) => _failures.Add(error);

    internal void Preserve(Exception? primary = null)
    {
        if (_failures.Count == 0) return;
        if (primary is not null)
        {
            primary.Data[Key] = Read(primary).Concat(_failures.Where(error => !ReferenceEquals(error, primary))).ToArray();
            return;
        }
        var first = _failures[0];
        if (_failures.Count > 1) first.Data[Key] = Read(first).Concat(_failures.Skip(1).Where(error => !ReferenceEquals(error, first))).ToArray();
        ExceptionDispatchInfo.Capture(first).Throw();
    }

    internal static IReadOnlyList<Exception> Read(Exception error) => error.Data[Key] as Exception[] ?? [];
}

internal sealed class HeldStorageResources : IDisposable
{
    private readonly List<IDisposable> _resources = [];

    internal void Add(IDisposable resource) => _resources.Add(resource);
    public void Dispose() => Release();

    internal void Release(Exception? primary = null)
    {
        var failures = new CleanupFailures();
        for (var index = _resources.Count - 1; index >= 0; index--) failures.Attempt(_resources[index].Dispose);
        _resources.Clear();
        failures.Preserve(primary);
    }
}
