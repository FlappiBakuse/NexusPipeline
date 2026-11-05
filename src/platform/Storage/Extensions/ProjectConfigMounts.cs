using System.Collections.Immutable;
using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility.Compilation;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed record ProjectConfigMount(string Path, ConfigurationRevision Saved, BlobReference Manifest);
internal sealed record CapturedConfigFile(string Path, ConfigurationReadStamp Source, PhysicalObjectIdentity Identity, BlobReference Content);

[SupportedOSPlatform("windows")]
internal sealed class ProjectConfigMounts : IDisposable
{
    private readonly SelectedProjectSource _source;
    private readonly InstanceLease _lease;
    private readonly RuntimeInstance _instance;
    private readonly ConfigurationRevisionStore _revisions;
    private readonly ImmutableBlobStore _blobs;
    private readonly ProjectReadLease _resources;
    private readonly List<IDisposable> _held = [];
    private readonly Dictionary<string, (CapturedConfigFile File, byte[] Bytes)> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly ImmutableArray<ConfigurationReadStamp> _markers;
    private long _bytes;
    private bool _disposed;
    internal ImmutableArray<ProjectConfigMount> Mounts { get; }
    internal IReadOnlyList<SourceConfiguration> Metadata
    {
        get
        {
            Check();
            return Array.AsReadOnly(Mounts.Select(item => new SourceConfiguration(item.Saved.Address.ResourceId, item.Path,
                item.Saved.Revision, item.Saved.Contract.Revision, item.Saved.Contract.DefinitionSha256, item.Manifest.Sha256,
                item.Saved.Tree.Shape == FileObjectKind.File ? SourceEntryKind.File : SourceEntryKind.Directory,
                item.Saved.Contract.RequiresInstance, item.Saved.Contract.InstanceKey)).ToArray());
        }
    }
    internal ImmutableArray<ConfigurationReadStamp> ReadSet => ConfigurationReadPolicy.Freeze(_markers.Concat(_files.Values.Select(item => item.File.Source))
        .DistinctBy(item => (item.Address, item.RelativePath)).ToArray());

    internal ProjectConfigMounts(RuntimeInstance instance, StateDatabase database, InstanceLease lease, ResourceLeaseManager resources,
        SelectedProjectSource source, ResourceOperationLease? actualRoot = null)
    {
        source.RequireOwner(instance, database, lease, resources);
        _source = source; _lease = lease; _instance = instance; _blobs = new(instance, new()); _revisions = new(instance, database, new());
        var binding = source.Binding; var script = source.Script; var mounts = ImmutableArray.CreateBuilder<ProjectConfigMount>();
        var markers = ImmutableArray.CreateBuilder<ConfigurationReadStamp>();
        foreach (var declaration in script.AdapterResources.OrderBy(item => item.DeclarationIndex))
        {
            var target = script.ConfigurationPaths[declaration.DeclarationIndex];
            if (target.RootId != script.RootId) throw new IOException("project_configuration_root_unbound");
            PhysicalPathPolicy.ValidateRelativePath(target.RelativePath);
            var saved = AdapterConfigurationPolicy.RequireSaved(binding, script, declaration, _revisions);
            saved = saved with { Tree = FileTreePolicy.Freeze(saved.Tree) };
            var entry = saved.Tree.Entries.Single(item => item.RelativePath == declaration.RelativePath && item.Kind == FileObjectKind.File);
            var stored = _revisions.ReadStored(saved.Address) ?? throw new IOException("project_configuration_unavailable");
            if (stored.Revision.Revision != saved.Revision || stored.Revision.Contract != saved.Contract) throw new IOException("project_configuration_changed");
            mounts.Add(new(target.RelativePath, saved, stored.Manifest));
            markers.Add(new(saved.Address, saved.Revision, binding.Revision, saved.Contract, entry.RelativePath, entry.Sha256!));
        }
        Mounts = mounts.ToImmutable(); _markers = ConfigurationReadPolicy.Freeze(markers);
        if (Mounts.Any(left => Mounts.Any(right => !ReferenceEquals(left, right) &&
            (left.Path.Equals(right.Path, StringComparison.OrdinalIgnoreCase) || left.Path.StartsWith(right.Path + "/", StringComparison.OrdinalIgnoreCase)))))
            throw new IOException("project_configuration_mount_overlap");
        var claims = _markers.Select(item => new ResourceClaim(ConfigurationRevisionService.ResourceKey(item.Address), ResourceClaimMode.Reference))
            .Append(new("blobs:content", ResourceClaimMode.Reference)).Append(new("automation:definitions", ResourceClaimMode.Reference));
        _resources = ProjectReadLease.Acquire(resources, claims, actualRoot);
        try
        {
            _held.Add(new ConfigurationReadLease(instance, _revisions, _blobs, _markers));
            foreach (var mount in Mounts) _held.Add(_blobs.OpenVerified(mount.Manifest));
            RequireCurrent();
        }
        catch { Dispose(); throw; }
    }

    internal bool TryRead(string relative, out SourceFile? file, CancellationToken cancellation)
    {
        Check(); cancellation.ThrowIfCancellationRequested(); PhysicalPathPolicy.ValidateRelativePath(relative); file = null;
        var mount = ProjectConfigDirectories.Match(Mounts, relative);
        if (mount is null)
        {
            if (Mounts.Any(item => item.Path.StartsWith(relative + "/", StringComparison.OrdinalIgnoreCase)))
                throw new IOException("project_configuration_shape_invalid");
            return false;
        }
        if (_files.TryGetValue(relative, out var previous))
        {
            if (previous.File.Path != relative) throw new IOException("project_configuration_alias_changed");
            file = Value(previous.File, previous.Bytes); return true;
        }
        var path = relative == mount.Path ? "" : relative[(mount.Path.Length + 1)..];
        var entry = mount.Saved.Tree.Entries.SingleOrDefault(item => item.RelativePath.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return true;
        if (entry.RelativePath != path || entry.Kind != FileObjectKind.File) throw new IOException("project_configuration_shape_invalid");
        if (_files.Count >= 128 || entry.SizeBytes is < 1 or > 8 * 1024 * 1024 || _bytes + entry.SizeBytes > 32 * 1024 * 1024)
            throw new IOException("project_configuration_source_limit");
        var reference = new BlobReference($"blobs/content/{entry.Sha256}.bin", entry.SizeBytes, entry.Sha256!);
        var input = _blobs.OpenVerified(reference);
        try
        {
            var bytes = new byte[checked((int)entry.SizeBytes)]; input.ReadExactly(bytes); cancellation.ThrowIfCancellationRequested();
            var stamp = new ConfigurationReadStamp(mount.Saved.Address, mount.Saved.Revision, _source.Binding.Revision,
                mount.Saved.Contract, path, entry.Sha256!);
            var captured = new CapturedConfigFile(relative, stamp, PhysicalObjectIdentity.Read(input.SafeFileHandle), reference);
            _source.ReserveInput(relative, bytes.Length);
            _files.Add(relative, (captured, bytes)); _bytes += bytes.LongLength; _held.Add(input); file = Value(captured, bytes); return true;
        }
        catch { input.Dispose(); throw; }
    }

    internal ImmutableArray<CapturedConfigFile> Capture()
    { RequireCurrent(); return _files.Values.Select(item => item.File).OrderBy(item => item.Path, StringComparer.Ordinal).ToImmutableArray(); }

    internal bool TryList(string relative, out SourceDirectory? directory)
    {
        Check(); if (relative.Length != 0) PhysicalPathPolicy.ValidateRelativePath(relative);
        return ProjectConfigDirectories.TryList(Mounts, relative, out directory);
    }
    internal SourceDirectory MergeDirectory(string relative, SourceDirectory original)
    { Check(); return ProjectConfigDirectories.Merge(Mounts, relative, original); }

    internal void RequireCurrent()
    {
        Check(); _source.RequireCurrent();
        foreach (var mount in Mounts)
        {
            if (_revisions.ReadCurrentRevision(mount.Saved.Address) == mount.Saved.Revision) continue;
            var current = _revisions.Read(mount.Saved.Address);
            if (current?.Revision != mount.Saved.Revision || current.Contract != mount.Saved.Contract || current.UserId != mount.Saved.UserId ||
                current.ScriptId != mount.Saved.ScriptId || current.Tree.Shape != mount.Saved.Tree.Shape ||
                !current.Tree.Entries.SequenceEqual(mount.Saved.Tree.Entries)) throw new IOException("project_configuration_changed");
        }
    }

    private SourceFile Value(CapturedConfigFile file, byte[] bytes) =>
        new(file.Path, file.Content.Sha256, ProjectConfigDirectories.Identity(Mounts.Single(item => item.Saved.Address == file.Source.Address),
            file.Source.RelativePath), bytes.ToArray());
    private void Check()
    {
        ObjectDisposedException.ThrowIf(_disposed, this); _lease.VerifyHeld(_instance); _source.RequireLive();
        if (!_resources.IsActive) throw new IOException("project_configuration_unavailable");
    }
    internal void RequireLive() => Check();
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        for (var index = _held.Count - 1; index >= 0; index--) _held[index].Dispose();
        _resources.Dispose();
    }
}
