using System.Collections.Concurrent;
using Microsoft.Win32.SafeHandles;

namespace NexusPipeline.Platform.Storage;

internal sealed class InstalledDirectoryMoveHandle : IDisposable
{
    private static readonly ConcurrentDictionary<string, InstalledDirectoryMoveHandle> Roots = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path;
    private SafeFileHandle? _handle;

    private InstalledDirectoryMoveHandle(string path) => _path = path;

    // A guarded root denies outside delete access. Reopening it for a host read must share the owner's existing DELETE access.
    internal static uint ReadShare(string path) => Roots.ContainsKey(path) ? 7u : 3u;

    internal static InstalledDirectoryMoveHandle Acquire(string path, PhysicalObjectIdentity expected)
    {
        var result = new InstalledDirectoryMoveHandle(path);
        if (!Roots.TryAdd(path, result)) throw new IOException("extension_directory_move_already_owned");
        try
        {
            result._handle = PhysicalObjectMove.OpenForMove(path);
            if (PhysicalObjectIdentity.Read(result._handle) != expected) throw new IOException("extension_directory_move_changed");
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    internal SafeFileHandle Handle => _handle ?? throw new ObjectDisposedException(nameof(InstalledDirectoryMoveHandle));

    public void Dispose()
    {
        Interlocked.Exchange(ref _handle, null)?.Dispose();
        Roots.TryRemove(new KeyValuePair<string, InstalledDirectoryMoveHandle>(_path, this));
    }
}
