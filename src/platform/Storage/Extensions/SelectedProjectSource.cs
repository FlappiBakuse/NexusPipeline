using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed record ProjectSourceEntry(string Name, FileObjectKind Kind, PhysicalObjectIdentity Identity);
internal sealed record ProjectDirectoryRead(string Path, bool Exists, PhysicalObjectIdentity? Identity, ImmutableArray<ProjectSourceEntry> Entries);
internal sealed record ProjectFileRead(string Path, PhysicalObjectIdentity Identity, string Sha256, ReadOnlyMemory<byte> Content);
internal sealed record SourceResourceArchive(string Path, ArchivedFileTree Archive);
internal sealed record CapturedProjectSource(Guid BindingId, long BindingRevision, long ScriptRevision, Guid RootId,
    long RootRevision, PhysicalObjectIdentity RootIdentity, ImmutableArray<ProjectDirectoryRead> Directories,
    ImmutableArray<(string Path, PhysicalObjectIdentity Identity, BlobReference Content)> Files,
    ImmutableArray<SourceResourceArchive> ResourceTrees);

[SupportedOSPlatform("windows")]
internal sealed class SelectedProjectSource : IDisposable
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly InstanceLease _lease;
    private readonly ImmutableBlobStore _blobs;
    private readonly Guid? _bindingId;
    private readonly Guid? _scriptId;
    private readonly string _extensionId;
    private readonly string _definitionDigest;
    private readonly AutomationBinding? _binding;
    private readonly AutomationScript _script;
    private readonly AutomationRoot _root;
    private readonly string _path;
    private readonly PhysicalObjectIdentity _identity;
    private readonly ProjectReadLease _resources;
    private readonly ResourceOperationLease? _actualRoot;
    private readonly ResourceLeaseManager _manager;
    private readonly Action _requireCaller;
    private readonly Dictionary<string, ProjectDirectoryRead> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (ProjectFileRead Read, FileStream Held)> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IDisposable> _held = [];
    private readonly HashSet<string> _missing = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SourceResourceArchive> _archives = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ProjectResourceSubset> _subsets = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _inputs = new(StringComparer.OrdinalIgnoreCase);
    private long _bytes;
    private long _definitionsRevision;
    private int _entries;
    private bool _disposed;
    private bool _sealed;
    internal AutomationBinding Binding => _binding ?? throw new IOException("project_binding_authority_required");
    internal AutomationScript Script => _script;
    internal AutomationRoot Root => _root;
    internal PhysicalObjectIdentity RootIdentity => _identity;
    internal bool IsScriptAuthority => _binding is null;

    internal void RequireOwner(RuntimeInstance instance, StateDatabase database, InstanceLease lease, ResourceLeaseManager resources)
    {
        if (!ReferenceEquals(instance, _instance) || !ReferenceEquals(database, _database) || !ReferenceEquals(lease, _lease) ||
            !ReferenceEquals(resources, _manager)) throw new IOException("project_source_owner_unbound");
        RequireCurrent();
    }

    internal SelectedProjectSource(RuntimeInstance instance, StateDatabase database, InstanceLease lease, ResourceLeaseManager resources,
        Guid bindingId, long expectedBindingRevision, string extensionId, Action requireCaller, ResourceOperationLease? actualRoot = null)
        : this(instance, database, lease, resources, bindingId, null, expectedBindingRevision, extensionId, requireCaller, actualRoot) { }

    internal static SelectedProjectSource ForScript(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
        ResourceLeaseManager resources, Guid scriptId, long expectedScriptRevision, string extensionId, Action requireCaller) =>
        new(instance, database, lease, resources, null, scriptId, expectedScriptRevision, extensionId, requireCaller, null);

    private SelectedProjectSource(RuntimeInstance instance, StateDatabase database, InstanceLease lease, ResourceLeaseManager resources,
        Guid? bindingId, Guid? scriptId, long expectedRevision, string extensionId, Action requireCaller, ResourceOperationLease? actualRoot)
    {
        _instance = instance; _database = database; _lease = lease; _blobs = new(instance, new()); _manager = resources;
        _bindingId = bindingId; _scriptId = scriptId; _extensionId = extensionId; _requireCaller = requireCaller; _actualRoot = actualRoot;
        lease.VerifyHeld(instance); requireCaller();
        (_binding, _script, _root) = ReadDefinitions();
        if ((_binding?.Revision ?? _script.Revision) != expectedRevision) throw Changed();
        _definitionDigest = DefinitionsDigest(_binding, _script, _root);
        _definitionsRevision = new AutomationDefinitionStore(_database).ReadRevision();
        _path = PhysicalPathPolicy.NormalizeWithoutLinks(_root.Path);
        if (!Directory.Exists(_path)) throw Changed();
        _identity = PhysicalObjectIdentity.Read(_path);
        var claims = PhysicalResourceAdmission.Read(_path, ResourceClaimMode.Reference, includeDescendants: false)
            .Concat([new ResourceClaim("automation:definitions", ResourceClaimMode.Reference), new("blobs:content", ResourceClaimMode.Reference),
                new("extension:" + extensionId, ResourceClaimMode.Reference)]);
        _resources = ProjectReadLease.Acquire(resources, claims, actualRoot);
        try { _held.Add(StableDirectoryLease.Acquire(_path)); RequireCurrent(); }
        catch { Dispose(); throw; }
    }

    internal ProjectDirectoryRead List(string relative)
    {
        RequireMutable(); Validate(relative, root: true);
        if (_directories.TryGetValue(relative, out var previous)) return SamePath(previous.Path, relative, previous);
        if (_directories.Count >= 128) throw Limit();
        if (relative.Length != 0)
        {
            var parent = List(Parent(relative)); var name = relative[(relative.LastIndexOf('/') + 1)..];
            var entry = parent.Entries.SingleOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (entry is not null && (entry.Name != name || entry.Kind != FileObjectKind.Directory)) throw Changed();
        }
        if (_directories.Count >= 128) throw Limit();
        var path = Resolve(relative);
        if (File.Exists(path)) throw Changed();
        if (!Directory.Exists(path))
        {
            var absent = new ProjectDirectoryRead(relative, false, null, []); _directories.Add(relative, absent); return absent;
        }
        var held = StableDirectoryLease.Acquire(path);
        try
        {
            var directory = Observe(relative, path);
            if (_entries + directory.Entries.Length > 4096) throw Limit();
            _entries += directory.Entries.Length; _directories.Add(relative, directory); _held.Add(held);
            return directory;
        }
        catch { held.Dispose(); throw; }
    }

    internal ProjectFileRead? ReadMetadata(string relative, CancellationToken cancellation)
    {
        RequireMutable(); cancellation.ThrowIfCancellationRequested(); Validate(relative, root: false);
        var parents = new Stack<string>();
        for (var parent = Parent(relative); parent.Length != 0; parent = Parent(parent)) parents.Push(parent);
        foreach (var parent in parents)
        {
            var directory = List(Parent(parent));
            var name = parent[(parent.LastIndexOf('/') + 1)..];
            var entry = directory.Entries.SingleOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            // An unrelated GUI Assets directory does not provide an exact assets metadata path.
            if (entry is not null && entry.Name != name) return null;
        }
        return Read(relative, cancellation);
    }

    internal ProjectFileRead? Read(string relative, CancellationToken cancellation)
    {
        RequireMutable(); cancellation.ThrowIfCancellationRequested(); Validate(relative, root: false);
        if (_files.TryGetValue(relative, out var previous))
            return SamePath(previous.Read.Path, relative, previous.Read with { Content = previous.Read.Content.ToArray() });
        var directory = List(Parent(relative));
        var name = relative[(relative.LastIndexOf('/') + 1)..];
        var entry = directory.Entries.SingleOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            if (_missing.Count >= 128 && !_missing.Contains(relative)) throw Limit();
            if (directory.Exists && !_missing.Contains(relative))
            { var held = HoldPhysical(Resolve(relative)); _held.Add(held); _missing.Add(relative); }
            return null;
        }
        if (entry.Name != name || entry.Kind != FileObjectKind.File) throw Changed();
        if (_inputs.Count >= 128) throw Limit();
        var path = Resolve(relative); var physical = HoldPhysical(path); FileStream? input = null;
        try
        {
            input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length is < 1 or > 8 * 1024 * 1024 || _bytes + input.Length > 32 * 1024 * 1024) throw Limit();
            if (PhysicalObjectIdentity.Read(input.SafeFileHandle) != entry.Identity) throw Changed();
            var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes); cancellation.ThrowIfCancellationRequested();
            if (PhysicalObjectIdentity.Read(path) != entry.Identity) throw Changed();
            var read = new ProjectFileRead(relative, entry.Identity, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes);
            ReserveInput(relative, bytes.Length); _files.Add(relative, (read, input)); _held.Add(physical);
            return read with { Content = bytes.ToArray() };
        }
        catch { input?.Dispose(); physical.Dispose(); throw; }
    }

    internal CapturedProjectSource Freeze(CancellationToken cancellation)
    {
        var binding = Binding;
        RequireMutable(); RequireCurrent();
        var files = ImmutableArray.CreateBuilder<(string Path, PhysicalObjectIdentity Identity, BlobReference Content)>();
        foreach (var file in _files.Values.OrderBy(item => item.Read.Path, StringComparer.Ordinal))
        {
            cancellation.ThrowIfCancellationRequested();
            var read = file.Read; var content = new BlobReference($"blobs/content/{read.Sha256}.bin", read.Content.Length, read.Sha256);
            file.Held.Position = 0; _blobs.PersistContent(file.Held, content, cancellation);
            _held.Add(_blobs.OpenVerified(content)); files.Add((read.Path, read.Identity, content));
        }
        RequireCurrent(); _sealed = true;
        return new(binding.Id, binding.Revision, _script.Revision, _root.Id, _root.Revision, _identity,
            _directories.Values.OrderBy(item => item.Path, StringComparer.Ordinal).ToImmutableArray(), files.ToImmutable(),
            _archives.Values.OrderBy(item => item.Path, StringComparer.Ordinal).ToImmutableArray());
    }

    internal SourceResourceArchive CaptureResource(string relative, CancellationToken cancellation)
        => CaptureResource(relative, null, cancellation);

    internal SourceResourceArchive CaptureResource(string relative, IReadOnlyList<string>? included, CancellationToken cancellation)
    {
        RequireMutable(); ProjectSubsetPolicy.Require(relative, included); cancellation.ThrowIfCancellationRequested();
        var subset = included?.Order(StringComparer.Ordinal).ToArray();
        if (_archives.TryGetValue(relative, out var previous))
        {
            _subsets.TryGetValue(relative, out var captured);
            if ((subset is null) != (captured is null) || subset is not null && !subset.SequenceEqual(captured!.IncludedPaths, StringComparer.Ordinal)) throw Changed();
            return SamePath(previous.Path, relative, previous);
        }
        if (_archives.Count >= (IsScriptAuthority ? 256 : 8)) throw Limit();
        if (subset is not null)
        {
            var captured = ProjectResourceSubset.Capture(Resolve(relative), subset, _manager, _actualRoot, _blobs, cancellation);
            if (_archives.Values.Sum(value => value.Archive.Snapshot.Entries.Sum(entry => entry.SizeBytes)) + captured.Archive.Snapshot.Entries.Sum(entry => entry.SizeBytes) > 1_073_741_824)
            { captured.Dispose(); throw Limit(); }
            var result = new SourceResourceArchive(relative, captured.Archive); _subsets.Add(relative, captured); _archives.Add(relative, result); _held.Add(captured); return result;
        }
        var path = Resolve(relative); var scanner = new FileTreeScanner(SnapshotBudget.Default);
        var before = scanner.CaptureArchive(path, cancellation);
        if (before.Shape == FileObjectKind.Missing || _archives.Values.Sum(item => item.Archive.Snapshot.Entries.Sum(entry => entry.SizeBytes)) + before.Entries.Sum(item => item.SizeBytes) > 1_073_741_824) throw Limit();
        var claims = PhysicalResourceAdmission.ReadTree(path, before); var held = new List<IDisposable>();
        try
        {
            foreach (var batch in claims.Chunk(4096))
                held.Add(ProjectReadLease.Acquire(_manager, batch, _actualRoot));
            var archive = new FileTreeArchive(scanner, _blobs).Capture(path, cancellation);
            if (archive.Snapshot.Shape != before.Shape || !archive.Snapshot.Entries.SequenceEqual(before.Entries) ||
                !PhysicalResourceAdmission.ReadTree(path, archive.Snapshot).SequenceEqual(claims)) throw Changed();
            held.Add(TreeReadLease.Acquire(path, PhysicalObjectIdentity.Read(Path.GetDirectoryName(path)!), archive));
            foreach (var content in archive.Contents) { cancellation.ThrowIfCancellationRequested(); held.Add(_blobs.OpenVerified(content)); }
            var result = new SourceResourceArchive(relative, archive); _archives.Add(relative, result); _held.AddRange(held); return result;
        }
        catch { for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose(); throw; }
    }

    internal void RequireCurrent()
    {
        RequireLive();
        if (PhysicalObjectIdentity.Read(_path) != _identity) throw Changed();
        var revision = new AutomationDefinitionStore(_database).ReadRevision();
        if (revision != _definitionsRevision)
        {
            var (binding, script, root) = ReadDefinitions();
            if (DefinitionsDigest(binding, script, root) != _definitionDigest) throw Changed();
            _definitionsRevision = revision;
        }
        foreach (var directory in _directories.Values)
        {
            var path = Resolve(directory.Path);
            if (File.Exists(path) || directory.Exists != Directory.Exists(path)) throw Changed();
            if (directory.Exists)
            {
                var current = Observe(directory.Path, path);
                if (current.Identity != directory.Identity || !current.Entries.SequenceEqual(directory.Entries)) throw Changed();
            }
        }
        foreach (var file in _files.Values)
            if (PhysicalObjectIdentity.Read(Resolve(file.Read.Path)) != file.Read.Identity ||
                PhysicalObjectIdentity.Read(file.Held.SafeFileHandle) != file.Read.Identity || file.Held.Length != file.Read.Content.Length) throw Changed();
        foreach (var resource in _archives.Values)
        {
            if (_subsets.TryGetValue(resource.Path, out var subset)) { subset.RequireCurrent(); continue; }
            var path = Resolve(resource.Path); var current = new FileTreeScanner(SnapshotBudget.Default).CaptureArchive(path, default);
            if (current.Shape != resource.Archive.Snapshot.Shape || !current.Entries.SequenceEqual(resource.Archive.Snapshot.Entries) ||
                !PhysicalTreeIdentity.Capture(path, current).SequenceEqual(resource.Archive.OriginalObjects)) throw Changed();
        }
    }

    private (AutomationBinding? Binding, AutomationScript Script, AutomationRoot Root) ReadDefinitions()
    {
        var catalog = new AutomationDefinitionStore(_database).Read();
        var binding = _bindingId is { } id ? catalog.Bindings.SingleOrDefault(item => item.Id == id) ?? throw Changed() : null;
        var script = catalog.Scripts.SingleOrDefault(item => item.Id == (binding?.ScriptId ?? _scriptId)) ?? throw Changed();
        var root = catalog.Roots.SingleOrDefault(item => item.Id == script.RootId) ?? throw Changed();
        if (script.Kind != AutomationKind.Specialized || script.ExtensionId != _extensionId || root.Kind != AutomationRootKind.Script ||
            binding is not null && !catalog.Users.Any(item => item.Id == binding.UserId)) throw Changed();
        return (binding, binding is null ? script : BindingScriptSource.Resolve(script, binding), root);
    }

    private ProjectDirectoryRead Observe(string relative, string path)
    {
        var identity = PhysicalObjectIdentity.Read(path);
        var entries = Directory.EnumerateFileSystemEntries(path).Take(4097).Select(entry =>
        {
            entry = PhysicalPathPolicy.NormalizeWithoutLinks(entry); var name = Path.GetFileName(entry); Validate(name, root: false);
            return new ProjectSourceEntry(name, Directory.Exists(entry) ? FileObjectKind.Directory : FileObjectKind.File, PhysicalObjectIdentity.Read(entry));
        }).OrderBy(item => item.Name, StringComparer.Ordinal).ToImmutableArray();
        if (entries.Length > 4096 || entries.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Length) throw Limit();
        if (PhysicalObjectIdentity.Read(path) != identity) throw Changed();
        return new(relative, true, identity, entries);
    }

    private string Resolve(string relative)
    {
        var path = relative.Length == 0 ? _path : Path.Combine(_path, relative.Replace('/', Path.DirectorySeparatorChar));
        var current = PhysicalPathPolicy.NormalizeWithoutLinks(path);
        if (current != _path && !current.StartsWith(_path + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw Changed();
        return current;
    }

    private void RequireMutable()
    { RequireLive(); if (_sealed) throw new InvalidOperationException("project_source_sealed"); }
    internal void RequireLive()
    {
        ObjectDisposedException.ThrowIf(_disposed, this); _lease.VerifyHeld(_instance); _requireCaller();
        if (!_resources.IsActive) throw Changed();
    }
    internal void ReserveInput(string relative, int bytes)
    {
        RequireMutable(); Validate(relative, root: false);
        if (_inputs.Contains(relative)) throw new IOException("project_source_alias_changed");
        if (_inputs.Count >= 128 || bytes is < 1 or > 8 * 1024 * 1024 || _bytes + bytes > 32 * 1024 * 1024) throw Limit();
        _inputs.Add(relative); _bytes += bytes;
    }
    private static string Parent(string relative) => relative.LastIndexOf('/') is var split && split >= 0 ? relative[..split] : "";
    private static T SamePath<T>(string original, string requested, T value) => original == requested ? value : throw Changed();
    private static void Validate(string relative, bool root)
    { ArgumentNullException.ThrowIfNull(relative); if (!(root && relative.Length == 0)) PhysicalPathPolicy.ValidateRelativePath(relative); }
    private ProjectReadLease HoldPhysical(string path) =>
        ProjectReadLease.Acquire(_manager, PhysicalResourceAdmission.Read(path, ResourceClaimMode.Reference, includeDescendants: false), _actualRoot);
    private static string DefinitionsDigest(AutomationBinding? binding, AutomationScript script, AutomationRoot root) =>
        Convert.ToHexStringLower(SHA256.HashData(BoundedJson.Serialize(new { binding, script, root })));
    private static IOException Changed() => new("project_source_changed");
    private static IOException Limit() => new("project_source_limit");

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        foreach (var file in _files.Values) file.Held.Dispose();
        for (var index = _held.Count - 1; index >= 0; index--) _held[index].Dispose();
        _resources.Dispose();
    }
}
