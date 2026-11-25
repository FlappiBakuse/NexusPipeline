using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NexusPipeline.Platform.Storage
{
    [SupportedOSPlatform("windows")]
    internal sealed class PhysicalDirectoryHandle(SafeFileHandle handle) : IDisposable
    {
        internal static PhysicalDirectoryHandle Open(string path, PhysicalObjectIdentity? expected = null)
        {
            PhysicalPathPolicy.NormalizeWithoutLinks(path);
            var actual = CreateFile(PhysicalPathPolicy.NativePath(path), 0x001000a0, InstalledDirectoryMoveHandle.ReadShare(path), IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            try
            {
                if (actual.IsInvalid) throw new IOException("physical_directory_unavailable", new Win32Exception(Marshal.GetLastWin32Error()));
                var identity = PhysicalObjectIdentity.Read(actual);
                if (!Directory.Exists(path) || identity != (expected ?? PhysicalObjectIdentity.Read(path)))
                    throw new IOException("physical_directory_identity_changed");
                return new(actual);
            }
            catch { actual.Dispose(); throw; }
        }

        internal PhysicalDirectoryHandle AcquireDirectory(string name)
        {
            var child = OpenChild(name, 0x001000a0, 3, 3, 0x00200021);
            try { PhysicalObjectIdentity.Read(child); return new(child); }
            catch { child.Dispose(); throw; }
        }

        internal PhysicalDirectoryHandle CreateNewDirectory(string name)
        {
            var child = OpenChild(name, 0x001000a0, 3, 2, 0x00200021);
            try { PhysicalObjectIdentity.Read(child); return new(child); }
            catch { child.Dispose(); throw; }
        }

        internal SafeFileHandle CreateNewFile(string name, bool writeThrough = false) =>
            OpenChild(name, 0x0012019f, 1, 2, writeThrough ? 0x00200062u : 0x00200060u);

        internal SafeFileHandle CreateNewOwnedFile(string name, bool writeThrough = false) =>
            OpenChild(name, 0x0013019f, 1, 2, writeThrough ? 0x00200062u : 0x00200060u);

        internal SafeFileHandle OpenFile(string name, bool write) => OpenChild(name,
            write ? 0x0012019fu : 0x00120089u, 1, write ? 3u : 1u, 0x00200060);

        internal SafeFileHandle OpenReadFile(string name) => OpenChild(name, 0x00120089, 7, 1, 0x00200060);
        internal SafeFileHandle OpenOwnedFile(string name) => OpenChild(name, 0x00130089, 1, 1, 0x00200060);

        internal PhysicalObjectIdentity ReadIdentity() => PhysicalObjectIdentity.Read(handle);

        private SafeFileHandle OpenChild(string name, uint access, uint share, uint disposition, uint options)
        {
            PhysicalPathPolicy.ValidateRelativePath(name);
            if (name.Contains('/')) throw new IOException("physical_file_component_invalid");
            var buffer = Marshal.StringToHGlobalUni(name); var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeName>());
            var added = false;
            try
            {
                handle.DangerousAddRef(ref added);
                var unicode = new UnicodeName { Length = checked((ushort)(name.Length * 2)), MaximumLength = checked((ushort)(name.Length * 2)), Buffer = buffer };
                Marshal.StructureToPtr(unicode, pointer, false);
                // Rooted handles and OBJ_DONT_REPARSE keep parent lookup from following a changed junction.
                var attributes = new ObjectAttributes { Length = (uint)Marshal.SizeOf<ObjectAttributes>(), Root = handle.DangerousGetHandle(), Name = pointer, Attributes = 0x1040 };
                var status = NtCreateFile(out var result, access, ref attributes, out _, IntPtr.Zero, 0, share, disposition, options, IntPtr.Zero, 0);
                if (status >= 0 && !result.IsInvalid) return result;
                result.Dispose(); throw new IOException("physical_file_unavailable", new Win32Exception(unchecked((int)RtlNtStatusToDosError(status))));
            }
            finally
            {
                if (added) handle.DangerousRelease();
                Marshal.FreeHGlobal(pointer); Marshal.FreeHGlobal(buffer);
            }
        }

        public void Dispose() => handle.Dispose();

        [StructLayout(LayoutKind.Sequential)]
        private struct UnicodeName { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }
        [StructLayout(LayoutKind.Sequential)]
        private struct ObjectAttributes { public uint Length; public IntPtr Root; public IntPtr Name; public uint Attributes; public IntPtr Security; public IntPtr Quality; }
        [StructLayout(LayoutKind.Sequential)]
        private struct IoStatus { public IntPtr Status; public UIntPtr Information; }

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("ntdll.dll")]
        private static extern int NtCreateFile(out SafeFileHandle result, uint access, ref ObjectAttributes attributes, out IoStatus status,
            IntPtr allocation, uint fileAttributes, uint share, uint disposition, uint options, IntPtr extra, uint extraLength);
        [DllImport("ntdll.dll")]
        private static extern uint RtlNtStatusToDosError(int status);
    }

    internal sealed class PhysicalDirectoryLease : IDisposable
    {
        private readonly PhysicalDirectoryHandle _handle;
        internal string Path { get; }
        internal PhysicalObjectIdentity Identity { get; }

        internal PhysicalDirectoryLease(string path, PhysicalObjectIdentity expected)
        {
            Path = PhysicalPathPolicy.NormalizeWithoutLinks(path);
            if (!Directory.Exists(Path)) throw new IOException("leased_directory_missing");
            _handle = PhysicalDirectoryHandle.Open(Path, expected);
            try
            {
                Identity = PhysicalObjectIdentity.Read(Path);
                if (Identity != expected || PhysicalObjectIdentity.Read(Path) != expected) throw new IOException("leased_directory_identity_changed");
            }
            catch { _handle.Dispose(); throw; }
        }

        public void Dispose() => _handle.Dispose();
    }

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
            var handle = CreateFile(PhysicalPathPolicy.NativePath(path), 0xa0, InstalledDirectoryMoveHandle.ReadShare(path), IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
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
}

namespace NexusPipeline.Platform.Storage.Journals
{
    internal static class ExclusiveDirectory
    {
        internal static void Create(string path)
        {
            PhysicalPathPolicy.NormalizeWithoutLinks(path);
            if (!CreateDirectory(PhysicalPathPolicy.NativePath(path), IntPtr.Zero))
                throw new IOException("journal_directory_not_created", new Win32Exception(Marshal.GetLastWin32Error()));
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateDirectory(string path, IntPtr securityAttributes);
    }
}
