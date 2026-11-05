using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace NexusPipeline.Platform.Storage;

[SupportedOSPlatform("windows")]
internal sealed class PhysicalDirectoryHandle(SafeFileHandle handle) : IDisposable
{
    internal static PhysicalDirectoryHandle Open(string path, PhysicalObjectIdentity? expected = null)
    {
        PhysicalPathPolicy.NormalizeWithoutLinks(path);
        var actual = CreateFile(PhysicalPathPolicy.NativePath(path), 0x001000a0, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
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

    internal SafeFileHandle CreateNewFile(string name) => OpenChild(name, 0x0012019f, 1, 2, 0x00200060);

    internal SafeFileHandle OpenFile(string name, bool write) => OpenChild(name,
        write ? 0x0012019fu : 0x00120089u, 1, write ? 3u : 1u, 0x00200060);

    internal SafeFileHandle OpenReadFile(string name) => OpenChild(name, 0x00120089, 7, 1, 0x00200060);

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
