using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Extensibility.Execution;

/// <summary>A borrowed Host directory for frozen inputs and separate worker outputs. Disposal never deletes files.</summary>
public sealed class ExecutionWorkDirectory : IDisposable
{
    private readonly List<SafeFileHandle> _held = [];
    private readonly List<(SafeFileHandle Handle, FileTreePhysicalIdentity Identity)> _identities = [];
    private readonly CancellationToken _stopping;
    private bool _disposed;
    /// <summary>The Host-owned directory, held against replacement for this borrow.</summary>
    public string Root { get; }
    /// <summary>The original Host input directory for materialized frozen files.</summary>
    public string InputDirectory { get; }
    /// <summary>The original Host output directory for native logs and generated files.</summary>
    public string OutputDirectory { get; }

    internal ExecutionWorkDirectory(ExecutionWorkspace grant, CancellationToken stopping)
    {
        _stopping = stopping;
        if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(grant.Path) ||
            grant.Path != Path.GetFullPath(grant.Path) || grant.Identity is null) throw Invalid();
        Root = grant.Path; InputDirectory = Path.Combine(Root, "inputs"); OutputDirectory = Path.Combine(Root, "outputs");
        try
        {
            var ancestors = new Stack<string>();
            for (var path = Root; path is not null; path = Path.GetDirectoryName(path)) ancestors.Push(path);
            foreach (var path in ancestors) Hold(path);
            if (_identities[^1].Identity != grant.Identity) throw Invalid();
            Hold(InputDirectory); if (_identities[^1].Identity != grant.InputIdentity) throw Invalid();
            Hold(OutputDirectory); if (_identities[^1].Identity != grant.OutputIdentity) throw Invalid();
            RequireCurrent();
        }
        catch { Dispose(); throw; }
    }

    /// <summary>Checks the original borrow and Host stop request before worker filesystem operations.</summary>
    public void RequireCurrent()
    {
        RequireHeld(); _stopping.ThrowIfCancellationRequested();
    }
    /// <summary>Checks original directory ownership during bounded native cleanup after input cancellation.</summary>
    public void RequireHeld()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var item in _identities) if (Read(item.Handle) != item.Identity) throw Invalid();
    }

    private void Hold(string path)
    {
        var handle = OpenDirectory(Native(path), 0x80, 3, 0, 3, 0x02200000, 0);
        if (handle.IsInvalid) { handle.Dispose(); throw Invalid(); }
        _held.Add(handle); _identities.Add((handle, Read(handle)));
    }
    private static string Native(string path) => path.StartsWith("\\\\", StringComparison.Ordinal) ?
        "\\\\?\\UNC\\" + path[2..] : "\\\\?\\" + path;
    private static FileTreePhysicalIdentity Read(SafeFileHandle handle)
    {
        if (!ReadTag(handle, 9, out var tag, 8) || (tag.Attributes & 0x410) != 0x10 ||
            !ReadId(handle, 18, out var id, 24)) throw Invalid();
        return new() { VolumeSerial = id.Volume.ToString("x16", CultureInfo.InvariantCulture),
            FileId = id.Low.ToString("x16", CultureInfo.InvariantCulture) + id.High.ToString("x16", CultureInfo.InvariantCulture) };
    }
    /// <summary>Releases directory handles after native writers have stopped; retains input and output bytes.</summary>
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        for (var index = _held.Count - 1; index >= 0; index--) _held[index].Dispose();
        _held.Clear(); _identities.Clear();
    }
    private static IOException Invalid() => new("execution_workspace_unbound");
    [StructLayout(LayoutKind.Sequential)] private struct DirectoryId { public ulong Volume; public ulong Low; public ulong High; }
    [StructLayout(LayoutKind.Sequential)] private struct DirectoryTag { public uint Attributes; public uint Tag; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenDirectory(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReadTag(SafeFileHandle handle, int kind, out DirectoryTag value, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReadId(SafeFileHandle handle, int kind, out DirectoryId value, uint size);
}
