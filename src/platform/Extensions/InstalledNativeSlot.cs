using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Buffers.Binary;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;

namespace NexusPipeline.Platform.Extensions;

internal sealed record InstalledNativeSource(string ExtensionId, Guid InstallationJournalId, string ArchiveSha256,
    string ManifestSha256, string TreeSha256, string TargetPath, PhysicalObjectIdentity TargetIdentity);
internal sealed record InstalledNativeMapping(string Path, long BaseAddress)
{
    internal static string NormalizePath(string path)
    {
        // LoadLibrary preserves the extended filename spelling; authorization uses the original package path.
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal) && path.Length > 6 && char.IsAsciiLetter(path[4]) &&
            path[5] == ':' && path[6] == '\\') path = path[4..];
        return PhysicalPathPolicy.NormalizeSyntax(path);
    }

    internal bool Matches(InstalledNativeMapping other) => BaseAddress == other.BaseAddress &&
        StringComparer.OrdinalIgnoreCase.Equals(Path, other.Path);
}

internal sealed class InstalledNativeSlot
{
    private readonly object _gate = new();
    private Residency? _resident;
    internal bool HasResidency { get { lock (_gate) return _resident is not null; } }

    internal void RequireSource(InstalledNativeSource source)
    {
        lock (_gate)
            if (_resident is { } current && current.Source != source) throw Restart();
    }

    internal bool Witnesses(string name, InstalledNativeMapping mapping)
    {
        lock (_gate) return _resident is { } resident && resident.Mappings.TryGetValue(name, out var original) && original.Matches(mapping);
    }

    internal void Retain(InstalledNativeSource source, ImmutableDictionary<string, InstalledNativeMapping> mappings,
        IDisposable files, Action unload)
    {
        if (mappings.IsEmpty) throw new IOException("extension_native_residency_empty");
        lock (_gate)
        {
            if (_resident is null) _resident = new(source, mappings, files, unload);
            else
            {
                if (_resident.Source != source || mappings.Any(pair => _resident.Mappings.TryGetValue(pair.Key, out var old) && !old.Matches(pair.Value)))
                    throw Restart();
                _resident.Mappings = _resident.Mappings.SetItems(mappings);
                unload(); files.Dispose();
            }
        }
    }

    internal void RequireNoResidency()
    {
        lock (_gate) if (_resident is not null) throw Restart();
    }

    [SupportedOSPlatform("windows")]
    internal static void RequireUnmappedTree(string targetPath)
    {
        var root = PhysicalPathPolicy.NormalizeSyntax(targetPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var process = Process.GetCurrentProcess();
        foreach (var module in process.Modules.Cast<ProcessModule>())
            if (InstalledNativeMapping.NormalizePath(module.FileName).StartsWith(root, StringComparison.OrdinalIgnoreCase) && !IsMappedManaged(process, module))
                throw Restart();
    }

    [SupportedOSPlatform("windows")]
    private static bool IsMappedManaged(Process process, ProcessModule module)
    {
        var bytes = new byte[Math.Min(4096, module.ModuleMemorySize)];
        if (bytes.Length < 256 || !ReadProcessMemory(process.Handle, module.BaseAddress, bytes, (nuint)bytes.Length, out var read) ||
            read != (nuint)bytes.Length || BinaryPrimitives.ReadUInt16LittleEndian(bytes) != 0x5a4d) return false;
        var pe = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x3c));
        if (pe < 64 || pe > bytes.Length - 256 || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pe)) != 0x4550) return false;
        var optional = pe + 24;
        var magic = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(optional));
        var directory = magic switch { 0x20b => optional + 112, 0x10b => optional + 96, _ => -1 };
        return directory >= 0 && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(directory + 14 * 8)) != 0;
    }

    private static IOException Restart() => new("extension_native_resident_restart_required");

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(nint process, nint address, [Out] byte[] buffer, nuint size, out nuint read);

    private sealed class Residency
    {
        internal InstalledNativeSource Source { get; }
        internal ImmutableDictionary<string, InstalledNativeMapping> Mappings { get; set; }
        private readonly IDisposable _files;
        private readonly Action _unload;
        internal Residency(InstalledNativeSource source, ImmutableDictionary<string, InstalledNativeMapping> mappings,
            IDisposable files, Action unload)
        {
            Source = source; Mappings = mappings; _files = files; _unload = unload;
            // Vendor references can outlive instance leases; retain the original input handles until this process exits.
            AppDomain.CurrentDomain.ProcessExit += KeepInputsAlive;
        }
        private void KeepInputsAlive(object? sender, EventArgs args)
        { GC.KeepAlive(_files); GC.KeepAlive(_unload); }
    }
}
