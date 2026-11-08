using System.Security.Cryptography;
using System.Text;
using NexusPipeline.Extensibility.Emulation;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Storage.Execution;

namespace NexusPipeline.Platform.Storage.Emulation;

internal sealed class EmulatorSourceLease : IDisposable
{
    private readonly List<IDisposable> _held = [];
    private readonly Dictionary<string, (FileStream File, string Sha)> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string[] Files, string[] Extensions)> _sets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Path, PhysicalObjectIdentity Identity)> _roots = [];
    private long _bytes;
    private bool _disposed;

    internal string HoldRoot(string requested)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(requested);
        if (_roots.TryGetValue(path, out var previous)) return Identity(previous.Identity);
        _held.Add(StableDirectoryLease.Acquire(path)); var identity = PhysicalObjectIdentity.Read(path);
        _roots.Add(path, (path, identity)); return Identity(identity);
    }

    internal (EmulatorTool Value, ExecutionPlanInterpreterImage Image) Tool(string path)
    {
        HoldRoot(Path.GetDirectoryName(path)!);
        var image = ExecutionHookImages.Capture(path, runtime: true);
        PlanInputLease.AcquireImage(image, _held);
        var physical = Identity(new(image.Identity.VolumeSerial, image.Identity.FileId));
        return (new("tool:" + Hash(path + "\n" + physical), image.Path, physical, image.Sha256, 1), image);
    }

    internal EmulatorConfig Config(string path, string relative)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        HoldRoot(Path.GetDirectoryName(path)!);
        if (!_files.TryGetValue(path, out var previous))
        {
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); _held.Add(file);
            PhysicalObjectIdentity.RequireSingleLink(path);
            if (_files.Count >= 4096 || file.Length > 2 * 1024 * 1024 || (_bytes += file.Length) > 32 * 1024 * 1024)
                throw new EmulatorException("emulator_config_size_limit");
            previous = (file, Convert.ToHexStringLower(SHA256.HashData(file))); _files.Add(path, previous);
        }
        previous.File.Position = 0; var bytes = new byte[checked((int)previous.File.Length)]; previous.File.ReadExactly(bytes);
        var physical = Identity(PhysicalObjectIdentity.Read(previous.File.SafeFileHandle));
        return new("config:" + Hash(path + "\n" + physical), relative, physical, previous.Sha, 1, bytes);
    }

    internal IReadOnlyList<string> ConfigFiles(string root, IReadOnlyList<string> extensions)
    {
        var found = Scan(root, extensions);
        if (_sets.TryGetValue(root, out var prior) && !prior.Files.SequenceEqual(found, StringComparer.OrdinalIgnoreCase))
            throw new EmulatorException("emulator_config_set_changed");
        _sets[root] = (found, extensions.ToArray()); return found;
    }

    internal string ConfigPath(EmulatorConfig config)
    {
        RequireCurrent();
        foreach (var (path, captured) in _files)
        {
            var physical = Identity(PhysicalObjectIdentity.Read(captured.File.SafeFileHandle));
            if (config.Id == "config:" + Hash(path + "\n" + physical) && config.PhysicalId == physical) return path;
        }
        throw new EmulatorException("emulator_config_unbound");
    }

    private string[] Scan(string root, IReadOnlyList<string> extensions)
    {
        var pending = new Stack<(string Path, int Depth)>(); pending.Push((root, 0));
        var files = new List<string>(); var count = 0;
        while (pending.TryPop(out var item))
        {
            if (item.Depth > 16) throw new EmulatorException("emulator_config_depth_limit");
            HoldRoot(item.Path);
            foreach (var entry in Directory.EnumerateFileSystemEntries(item.Path))
            {
                if (++count > 16384) throw new EmulatorException("emulator_config_entry_limit");
                var path = PhysicalPathPolicy.NormalizeWithoutLinks(entry);
                if (Directory.Exists(path)) pending.Push((path, item.Depth + 1));
                else if (extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) files.Add(path);
            }
        }
        return files.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal void RequireCurrent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var item in _roots.Values)
            if (PhysicalObjectIdentity.Read(item.Path) != item.Identity) throw new EmulatorException("emulator_root_replaced");
        foreach (var item in _files.Values)
        {
            item.File.Position = 0;
            if (Convert.ToHexStringLower(SHA256.HashData(item.File)) != item.Sha) throw new EmulatorException("emulator_config_changed");
        }
        foreach (var (path, expected) in _sets.ToArray())
            if (!expected.Files.SequenceEqual(Scan(path, expected.Extensions), StringComparer.OrdinalIgnoreCase)) throw new EmulatorException("emulator_config_set_changed");
    }

    internal static string Identity(PhysicalObjectIdentity identity) => identity.VolumeSerial + ":" + identity.FileId;
    internal static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        for (var index = _held.Count - 1; index >= 0; index--) _held[index].Dispose();
    }
}
