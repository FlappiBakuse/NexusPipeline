using System.Text.Json;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage;

public sealed class InstanceRoot
{
    private readonly ContractReader _contracts;

    public InstanceRoot(ContractReader contracts)
    {
        _contracts = contracts;
    }

    public RuntimeInstance OpenOrCreate(string requestedRoot)
    {
        if (OperatingSystem.IsWindows()) Installation.InstallerRuntimeGate.RequireAvailable(requestedRoot);
        return OpenCore(requestedRoot);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal RuntimeInstance OpenInstaller(string requestedRoot, NexusPipeline.Platform.Processes.ProcessIdentity setup)
    {
        Installation.InstallerCheckpointStore.RequireCaller(setup);
        return OpenCore(requestedRoot);
    }

    private RuntimeInstance OpenCore(string requestedRoot)
    {
        var root = ResolveRoot(requestedRoot);
        var identityPath = Path.Combine(root, "instance.json");
        if (File.Exists(identityPath))
            return ReadExisting(root);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new IOException("nonempty_unrecognized_runtime");
        Directory.CreateDirectory(root);
        var created = new Instance
        {
            Format = "nexus.instance", Version = 1, InstanceId = Guid.NewGuid().ToString("D"),
            RuntimeRoot = root, CreatedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'")
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(created);
        using var validated = _contracts.Read(bytes, "instance.schema.json");
        using var stream = new FileStream(identityPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
        return ConvertIdentity(created);
    }

    public RuntimeInstance OpenExisting(string requestedRoot) => ReadExisting(ResolveRoot(requestedRoot));

    private static string ResolveRoot(string requestedRoot)
    {
        return PhysicalPathPolicy.NormalizeWithoutLinks(requestedRoot);
    }

    private RuntimeInstance ReadExisting(string root)
    {
        var identityPath = Path.Combine(root, "instance.json");
        if ((File.GetAttributes(identityPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("invalid_runtime_identity");
        using var input = new FileStream(identityPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > StrictJson.MaximumBytes)
            throw new IOException("invalid_runtime_identity");
        var identityBytes = new byte[checked((int)input.Length)];
        input.ReadExactly(identityBytes);
        var identity = _contracts.Read<Instance>(identityBytes, "instance.schema.json");
        if (!string.Equals(root, identity.RuntimeRoot, StringComparison.OrdinalIgnoreCase))
            throw new IOException("runtime_identity_path_mismatch");
        return ConvertIdentity(identity);
    }

    private static RuntimeInstance ConvertIdentity(Instance identity) => new(
        Guid.ParseExact(identity.InstanceId, "D"), identity.RuntimeRoot,
        DateTimeOffset.Parse(identity.CreatedAt, System.Globalization.CultureInfo.InvariantCulture));
}
