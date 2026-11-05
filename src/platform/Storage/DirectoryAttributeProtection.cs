using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NexusPipeline.Platform.Storage;

internal sealed class DirectoryAttributeProtection : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SafeFileHandle> _directories = new(StringComparer.Ordinal);
    private bool _disposed;

    private DirectoryAttributeProtection() { }

    internal static DirectoryAttributeProtection Acquire(RuntimeInstance instance)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var result = new DirectoryAttributeProtection();
        try
        {
            using var scope = DirectoryAttributeScope.Enter(result);
            var selected = new[] { instance.RuntimeRoot, Path.Combine(instance.RuntimeRoot, "blobs", "plans"),
                Path.Combine(instance.RuntimeRoot, "blobs", "content"), Path.Combine(instance.RuntimeRoot, "journals"),
                Path.Combine(instance.RuntimeRoot, "state"), Path.Combine(instance.RuntimeRoot, "secrets") };
            var ancestors = new HashSet<string>(StringComparer.Ordinal);
            foreach (var directory in selected)
                for (var path = PhysicalPathPolicy.NormalizeWithoutLinks(directory); path is not null; path = Path.GetDirectoryName(path))
                    ancestors.Add(path);
            foreach (var path in ancestors.OrderBy(path => path.Length))
            {
                if (result._directories.Count == 64) break;
                result.Retain(path);
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    internal static DirectoryAttributeProtection AcquireDirectories(IEnumerable<string> directories)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var result = new DirectoryAttributeProtection();
        try
        {
            using var scope = DirectoryAttributeScope.Enter(result);
            result.Include(directories.Distinct(StringComparer.Ordinal));
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    internal void Include(IEnumerable<string> directories)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var directory in directories)
            {
                var ancestors = new Stack<string>();
                for (var path = PhysicalPathPolicy.NormalizeWithoutLinks(directory); path is not null; path = Path.GetDirectoryName(path))
                    ancestors.Push(path);
                while (ancestors.TryPop(out var path))
                {
                    if (_directories.Count == 64) return;
                    if (!_directories.ContainsKey(path)) Retain(path);
                }
            }
        }
    }

    internal bool TryReadCurrent(string path, out FileAttributes attributes)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_directories.TryGetValue(path, out var handle))
            {
                attributes = Read(handle);
                return true;
            }
            attributes = default;
            return false;
        }
    }

    private void Retain(string path)
    {
        // Attribute-only access does not participate in delete sharing; directory traverse access pins the name.
        var handle = CreateFile(PhysicalPathPolicy.NativePath(path), 0xa0, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error(); handle.Dispose();
            if (error is 2 or 3) return;
            throw new IOException("directory_attributes_unavailable", new Win32Exception(error));
        }
        try
        {
            if ((Read(handle, requireDirectory: false) & FileAttributes.Directory) == 0) { handle.Dispose(); return; }
            lock (_gate) _directories.Add(path, handle);
        }
        catch { handle.Dispose(); throw; }
    }

    private static FileAttributes Read(SafeFileHandle handle, bool requireDirectory = true)
    {
        if (!ReadTag(handle, 9, out var info, 8))
            throw new IOException("directory_attributes_unavailable", new Win32Exception(Marshal.GetLastWin32Error()));
        var attributes = (FileAttributes)info.Attributes;
        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("filesystem_reparse_point_rejected");
        if (requireDirectory && (attributes & FileAttributes.Directory) == 0) throw new IOException("directory_attributes_shape_changed");
        return attributes;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var handle in _directories.OrderByDescending(item => item.Key.Length).Select(item => item.Value)) handle.Dispose();
            _directories.Clear();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTag { public uint Attributes; public uint Tag; }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadTag(SafeFileHandle handle, int informationClass, out AttributeTag information, uint size);
}
