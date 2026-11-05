namespace NexusPipeline.Platform.Extensions;

internal sealed class InstalledNativeLifetime
{
    private readonly IDisposable _files;
    private readonly Action _unload;
    private int _released;

    internal InstalledNativeLifetime(IDisposable files, Action unload)
    {
        _files = files; _unload = unload;
        // A failed resource destructor can retain native code even after its instance inventory becomes unreachable.
        AppDomain.CurrentDomain.ProcessExit += KeepInputsAlive;
    }

    internal void Transfer()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
            AppDomain.CurrentDomain.ProcessExit -= KeepInputsAlive;
    }

    internal void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0) return;
        try { _unload(); }
        finally
        {
            _files.Dispose();
            AppDomain.CurrentDomain.ProcessExit -= KeepInputsAlive;
        }
    }

    private void KeepInputsAlive(object? sender, EventArgs args)
    { GC.KeepAlive(_files); GC.KeepAlive(_unload); }
}
