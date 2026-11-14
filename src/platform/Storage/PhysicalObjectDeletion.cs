using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage;

public sealed record FileObjectDeletion(string Path, PhysicalObjectIdentity ParentIdentity, PhysicalObjectIdentity Identity,
    FileObjectKind Kind, long SizeBytes, string? Sha256);

public static class PhysicalObjectDeletion
{
    public static void DeleteVerified(FileObjectDeletion intent)
    {
        _ = Delete(intent, static () => true);
    }

    internal static void DeleteInspectionCopy(FileObjectDeletion intent)
    {
        if (intent.Kind != FileObjectKind.File) throw new ArgumentException("An inspection file is required");
        _ = Delete(intent, static () => true, long.MaxValue);
    }

    public static bool TryDeleteUnreferenced(FileObjectDeletion intent, Func<bool> isUnreferenced)
    {
        if (intent.Kind != FileObjectKind.File) throw new ArgumentException("A content file is required");
        ArgumentNullException.ThrowIfNull(isUnreferenced);
        return Delete(intent, isUnreferenced, singleLink: true);
    }

    internal static FileLease HoldFile(FileObjectDeletion intent)
    {
        DirectoryAttributeScope.Refresh();
        Validate(intent, 1_073_741_824);
        if (intent.Kind != FileObjectKind.File) throw new ArgumentException("A content file is required");
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(intent.Path);
        var parent = Path.GetDirectoryName(path) ?? throw new IOException("deletion_root_rejected");
        var directory = StableDirectoryLease.Acquire(parent);
        SafeFileHandle? handle = null;
        FileStream? source = null;
        try
        {
            if (PhysicalObjectIdentity.Read(parent) != intent.ParentIdentity) throw new IOException("deletion_parent_identity_changed");
            handle = OpenObject(intent, path, FileShare.Read);
            PhysicalObjectIdentity.RequireSingleLink(handle);
            source = new FileStream(handle, FileAccess.Read);
            VerifyFile(intent, source);
            return new(intent, parent, directory, source);
        }
        catch { source?.Dispose(); handle?.Dispose(); directory.Dispose(); throw; }
    }

    private static bool Delete(FileObjectDeletion intent, Func<bool> authorize, long maximumBytes = 1_073_741_824,
        bool singleLink = false)
    {
        DirectoryAttributeScope.Refresh();
        Validate(intent, maximumBytes);
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(intent.Path);
        var parent = Path.GetDirectoryName(path) ?? throw new IOException("deletion_root_rejected");
        using var parentLease = StableDirectoryLease.Acquire(parent);
        if (PhysicalObjectIdentity.Read(parent) != intent.ParentIdentity) throw new IOException("deletion_parent_identity_changed");
        using var handle = OpenObject(intent, path, FileShare.Read | FileShare.Delete);
        if (intent.Kind == FileObjectKind.File)
        {
            if (singleLink) PhysicalObjectIdentity.RequireSingleLink(handle);
            using var source = new FileStream(handle, FileAccess.Read);
            VerifyFile(intent, source);
            if (!authorize()) return false;
            MarkDeleted(handle);
        }
        else
        {
            if (Directory.EnumerateFileSystemEntries(path).Any()) throw new IOException("deletion_directory_not_empty");
            MarkDeleted(handle);
        }
        return true;
    }

    private static void Validate(FileObjectDeletion intent, long maximumBytes)
    {
        if (intent.Kind is not (FileObjectKind.File or FileObjectKind.Directory) || intent.SizeBytes < 0 || intent.SizeBytes > maximumBytes ||
            intent.Kind == FileObjectKind.Directory && (intent.SizeBytes != 0 || intent.Sha256 is not null) ||
            intent.Kind == FileObjectKind.File && (intent.Sha256 is null || intent.Sha256.Length != 64))
            throw new ArgumentException("Invalid exact-object deletion intent");
    }

    private static SafeFileHandle OpenObject(FileObjectDeletion intent, string path, FileShare share)
    {
        var access = intent.Kind == FileObjectKind.File ? 0x80010080u : 0x00010080u;
        var handle = CreateFile(PhysicalPathPolicy.NativePath(path), access, (uint)share, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        try
        {
            if (handle.IsInvalid) throw Failure("deletion_open_failed");
            if (PhysicalObjectIdentity.Read(handle) != intent.Identity) throw new IOException("deletion_object_identity_changed");
            if (!ReadAttributes(handle, 9, out var tag, 8)) throw Failure("deletion_attributes_unavailable");
            if (((tag.Attributes & 0x10) != 0) != (intent.Kind == FileObjectKind.Directory))
                throw new IOException("deletion_object_shape_changed");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    private static void VerifyFile(FileObjectDeletion intent, FileStream source)
    {
        if (source.Length != intent.SizeBytes || Convert.ToHexStringLower(SHA256.HashData(source)) != intent.Sha256)
            throw new IOException("deletion_object_bytes_changed");
    }

    internal sealed class FileLease(FileObjectDeletion owned, string parent, StableDirectoryLease directory, FileStream source) : IDisposable
    {
        internal void Require(FileObjectDeletion intent)
        {
            if (intent != owned || PhysicalObjectIdentity.Read(parent) != intent.ParentIdentity ||
                PhysicalObjectIdentity.Read(source.SafeFileHandle) != intent.Identity)
                throw new IOException("deletion_file_lease_unbound");
            PhysicalObjectIdentity.RequireSingleLink(source.SafeFileHandle);
        }

        internal bool TryDelete(FileObjectDeletion intent, Func<bool> authorize)
        {
            DirectoryAttributeScope.Refresh();
            Require(intent);
            if (!authorize()) return false;
            MarkDeleted(source.SafeFileHandle);
            source.Dispose();
            return true;
        }

        public void Dispose() { source.Dispose(); directory.Dispose(); }
    }

    private static void MarkDeleted(SafeFileHandle handle)
    {
        byte delete = 1;
        if (!SetDisposition(handle, 4, ref delete, 1)) throw Failure("deletion_disposition_failed");
    }

    private static IOException Failure(string code) => new(code, new Win32Exception(Marshal.GetLastWin32Error()));

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTag { public uint Attributes; public uint ReparseTag; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadAttributes(SafeFileHandle handle, int informationClass, out AttributeTag information, uint size);
    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDisposition(SafeFileHandle handle, int informationClass, ref byte information, uint size);
}
