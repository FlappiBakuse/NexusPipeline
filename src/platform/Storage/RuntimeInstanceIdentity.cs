using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using System.Text.Json;

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
        if (InstanceReadScope.Read(root) is { } held) return held;
        var identityPath = Path.Combine(root, "instance.json");
        if ((File.GetAttributes(identityPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("invalid_runtime_identity");
        using var input = new FileStream(identityPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1);
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

internal sealed class InstanceReadScope : IDisposable
{
    [ThreadStatic] private static InstanceReadScope? _current;
    private readonly InstanceReadScope? _previous = _current;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly RuntimeInstance _instance;
    private readonly StableDirectoryLease? _directory;
    private readonly FileStream? _identity;
    private readonly InstanceLease? _lease;
    private bool _disposed;

    private InstanceReadScope(RuntimeInstance instance, ContractReader contracts)
    {
        _instance = instance;
        if (_previous?._instance == instance)
        { _lease = _previous._lease; _lease?.VerifyHeld(instance); _current = this; return; }
        _directory = StableDirectoryLease.Acquire(instance.RuntimeRoot);
        try
        {
            var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, "instance.json"));
            _identity = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                _ = PhysicalObjectIdentity.Read(_identity.SafeFileHandle);
                if (new InstanceRoot(contracts).OpenExisting(instance.RuntimeRoot) != instance)
                    throw new IOException("runtime_identity_changed");
                _current = this;
            }
            catch { _identity.Dispose(); throw; }
        }
        catch { _directory.Dispose(); throw; }
    }

    private InstanceReadScope(RuntimeInstance instance, InstanceLease lease)
    {
        lease.VerifyHeld(instance);
        _instance = instance; _lease = lease; _current = this;
    }

    internal static InstanceReadScope Hold(RuntimeInstance instance, ContractReader contracts) => new(instance, contracts);
    internal static InstanceReadScope Hold(RuntimeInstance instance, InstanceLease lease) => new(instance, lease);

    internal static RuntimeInstance? Read(string root)
    {
        if (_current is not { } scope || !StringComparer.OrdinalIgnoreCase.Equals(root, scope._instance.RuntimeRoot)) return null;
        scope._lease?.VerifyHeld(scope._instance);
        // The identity denies writes/deletion and the directory leases pin every ancestor name.
        return scope._instance;
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_thread != Environment.CurrentManagedThreadId || !ReferenceEquals(_current, this))
            throw new InvalidOperationException("instance_read_scope_order");
        _disposed = true; _current = _previous;
        try { _identity?.Dispose(); } finally { _directory?.Dispose(); }
    }
}
