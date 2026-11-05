using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage;

internal sealed record FileObjectMove(string Source, PhysicalObjectIdentity SourceParent,
    PhysicalObjectIdentity SourceIdentity, string Destination, PhysicalObjectIdentity DestinationParent,
    FileObjectKind Kind);

internal static class PhysicalObjectMove
{
    internal static void MoveVerified(FileObjectMove intent)
    {
        if (intent.Kind is not (FileObjectKind.File or FileObjectKind.Directory)) throw new ArgumentException("physical_move_kind_invalid");
        var source = PhysicalPathPolicy.NormalizeWithoutLinks(intent.Source);
        var destination = PhysicalPathPolicy.NormalizeWithoutLinks(intent.Destination);
        if (StringComparer.OrdinalIgnoreCase.Equals(source, destination) ||
            intent.SourceParent.VolumeSerial != intent.DestinationParent.VolumeSerial ||
            intent.SourceIdentity.VolumeSerial != intent.SourceParent.VolumeSerial) throw new IOException("physical_move_scope_invalid");
        var sourceParent = Path.GetDirectoryName(source) ?? throw new IOException("physical_move_root_rejected");
        var destinationParent = Path.GetDirectoryName(destination) ?? throw new IOException("physical_move_root_rejected");
        using var sourceLease = StableDirectoryLease.Acquire(sourceParent);
        using var destinationLease = StableDirectoryLease.Acquire(destinationParent);
        using var sourceParentHandle = Open(sourceParent, 0x001000a0);
        using var destinationParentHandle = Open(destinationParent, 0x001000a6);
        if (PhysicalObjectIdentity.Read(sourceParentHandle) != intent.SourceParent ||
            PhysicalObjectIdentity.Read(destinationParentHandle) != intent.DestinationParent)
            throw new IOException("physical_move_parent_changed");
        using var entry = Open(source, 0x00110080);
        if (PhysicalObjectIdentity.Read(entry) != intent.SourceIdentity) throw new IOException("physical_move_object_changed");
        if (!ReadAttributes(entry, 9, out var attributes, 8)) throw Failure("physical_move_attributes_unavailable");
        if (((attributes.Attributes & 0x10) != 0) != (intent.Kind == FileObjectKind.Directory))
            throw new IOException("physical_move_shape_changed");
        Rename(entry, destinationParentHandle, Path.GetFileName(destination));
        if (PhysicalObjectIdentity.Read(entry) != intent.SourceIdentity ||
            PhysicalObjectIdentity.Read(destination) != intent.SourceIdentity)
            throw new IOException("physical_move_result_unconfirmed");
    }

    private static SafeFileHandle Open(string path, uint access)
    {
        var result = CreateFile(PhysicalPathPolicy.NativePath(path), access, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (!result.IsInvalid) return result;
        var error = Marshal.GetLastWin32Error(); result.Dispose();
        throw new IOException("physical_move_handle_unavailable", new Win32Exception(error));
    }

    private static void Rename(SafeFileHandle source, SafeFileHandle destinationParent, string name)
    {
        PhysicalPathPolicy.ValidateRelativePath(name);
        if (name.Contains('/')) throw new IOException("physical_move_component_invalid");
        var bytes = Encoding.Unicode.GetBytes(name);
        var nameOffset = checked((int)Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.FirstCharacter)));
        var rootOffset = checked((int)Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.RootDirectory)));
        var lengthOffset = checked((int)Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.NameLength)));
        var length = checked(Marshal.SizeOf<RenameInformation>() + bytes.Length);
        var buffer = Marshal.AllocHGlobal(length);
        var held = false;
        try
        {
            for (var index = 0; index < length; index++) Marshal.WriteByte(buffer, index, 0);
            destinationParent.DangerousAddRef(ref held);
            Marshal.WriteIntPtr(buffer, rootOffset, destinationParent.DangerousGetHandle());
            Marshal.WriteInt32(buffer, lengthOffset, bytes.Length);
            Marshal.Copy(bytes, 0, IntPtr.Add(buffer, nameOffset), bytes.Length);
            // The NT entry point keeps the rename relative to the pinned destination handle.
            var status = SetInformation(source, out _, buffer, checked((uint)length), 10);
            if (status != 0) throw new IOException("physical_move_rename_rejected",
                new Win32Exception(unchecked((int)RtlNtStatusToDosError(status))));
        }
        finally
        {
            if (held) destinationParent.DangerousRelease();
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IOException Failure(string code) => new(code, new Win32Exception(Marshal.GetLastWin32Error()));
    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTag { public uint Attributes; public uint ReparseTag; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RenameInformation { public uint Flags; public IntPtr RootDirectory; public uint NameLength; public ushort FirstCharacter; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadAttributes(SafeFileHandle handle, int informationClass, out AttributeTag information, uint size);
    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatus { public IntPtr Status; public UIntPtr Information; }
    [DllImport("ntdll.dll", EntryPoint = "NtSetInformationFile")]
    private static extern int SetInformation(SafeFileHandle handle, out IoStatus status, IntPtr information, uint size, int informationClass);
    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);
}
