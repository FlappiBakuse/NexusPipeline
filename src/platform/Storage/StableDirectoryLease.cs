using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NexusPipeline.Platform.Storage;

internal sealed class StableDirectoryLease : IDisposable
{
    private readonly List<SafeFileHandle> _handles = [];

    internal static StableDirectoryLease Acquire(string requestedDirectory)
    {
        var directory = PhysicalPathPolicy.NormalizeWithoutLinks(requestedDirectory);
        var ancestors = new Stack<string>();
        for (var current = directory; current is not null; current = Path.GetDirectoryName(current)) ancestors.Push(current);
        var lease = new StableDirectoryLease();
        try
        {
            foreach (var path in ancestors)
                lease._handles.Add(OpenEntry(path, out _));
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    internal static SafeFileHandle OpenEntry(string path, out PhysicalObjectIdentity identity)
    {
        var handle = CreateFile(PhysicalPathPolicy.NativePath(path), 0xa0, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        try
        {
            if (handle.IsInvalid)
                throw new IOException("directory_lease_unavailable", new Win32Exception(Marshal.GetLastWin32Error()));
            identity = PhysicalObjectIdentity.Read(handle);
            if (!Directory.Exists(path)) throw new IOException("directory_lease_shape_changed");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    public void Dispose()
    {
        for (var index = _handles.Count - 1; index >= 0; index--) _handles[index].Dispose();
        _handles.Clear();
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
}
