using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class ResolvedAdapterSelection : IDisposable
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly InstanceLease _lease;
    private readonly ContractReader _contracts;
    private readonly PayloadApplyRoots _roots;
    private readonly string _definitionDigest;
    private readonly List<IDisposable> _held = [];
    private readonly ExtensionInstalledMetadata _installed;
    private bool _disposed;
    private SelectedProjectSource? _source;
    private readonly ResourceLeaseManager _manager;
    private readonly IDisposable _rootOwnership;
    private readonly Action _requireCaller;
    internal ExtensionInstalledMetadata Installed => _installed;
    internal IReadOnlyList<ConfigurationRevision> Configurations { get; }
    internal AdapterSelection Selection { get; }
    internal ResourceOperationLease Root { get; }
    internal AdapterSourceStamp Stamp { get; private set; }
    internal ImmutableArray<TaskDefinitionsResource> Resources { get; }

    internal string CheckGamePath()
    {
        RequireCurrent();
        var installed = new ExtensionInstalledEvidence(_instance, _database, _contracts, _roots).ReadCurrent(Selection.ExtensionId)
            ?? throw Invalid();
        var policy = ExtensionDetailsReader.ReadListing(installed, _contracts).Specialized?.InstancePolicy;
        if (policy?.GamePathResource is null) return "notApplicable";
        var resource = Resources.SingleOrDefault(item => item.ResourceId == policy.GamePathResource);
        var (_, script) = ReadDefinitions();
        if (resource is null || script.Target?.Pc is not { } pc) return "unverified";
        using var document = StrictJson.Parse(System.Text.Encoding.UTF8.GetBytes(resource.Content));
        var value = document.RootElement;
        foreach (var field in policy.GamePathSelector!)
            if (value.ValueKind != System.Text.Json.JsonValueKind.Object || !value.TryGetProperty(field, out value)) return "unverified";
        if (value.ValueKind != System.Text.Json.JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) return "unverified";
        var root = new AutomationDefinitionStore(_database).Read().Roots.Single(item => item.Id == pc.ReadyExecutable.RootId);
        try
        {
            var actual = PhysicalPathPolicy.NormalizeSyntax(value.GetString()!);
            var expected = PhysicalPathPolicy.NormalizeSyntax(Path.Combine(root.Path, pc.ReadyExecutable.RelativePath));
            return StringComparer.OrdinalIgnoreCase.Equals(actual, expected) ? "matches" : "mismatch";
        }
        catch (Exception error) when (error is IOException or ArgumentException) { return "unverified"; }
    }

    internal ResolvedAdapterSelection(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
        ResourceLeaseManager resources, ContractReader contracts, PayloadApplyRoots roots, AdapterSelection selection,
        ResourceOperationLease? actualRoot = null, Action? requireCaller = null)
    {
        _manager = resources;
        Selection = AdapterSelectionPolicy.Freeze(selection);
        _instance = instance; _database = database; _lease = lease; _contracts = contracts; _roots = roots;
        _requireCaller = requireCaller ?? (() => { }); _requireCaller();
        lease.VerifyHeld(instance);
        var claims = new List<ResourceClaim>
        {
            new("automation:definitions", ResourceClaimMode.Reference), new("blobs:content", ResourceClaimMode.Reference),
            new("settings:host", ResourceClaimMode.Reference), new("extension:" + selection.ExtensionId, ResourceClaimMode.Reference)
        };
        claims.AddRange(selection.Resources.Select(resource => new ResourceClaim(
            ConfigurationRevisionService.ResourceKey(new(selection.BindingId, resource.ResourceId)), ResourceClaimMode.Reference)));
        if (actualRoot is null)
        {
            Root = resources.TryAcquire(selection.OperationId, claims).Lease ?? throw new IOException("adapter_resources_unavailable");
            _rootOwnership = Root;
        }
        else
        {
            _rootOwnership = actualRoot.Borrow(resources, selection.OperationId, claims); Root = actualRoot;
        }
        try
        {
            var (binding, script) = ReadDefinitions();
            AutomationAdapterPolicy.Validate(script);
            if (script.AdapterResources.Length != selection.Resources.Length || selection.Resources.Any(selected =>
                !script.AdapterResources.Any(declared => declared.ResourceId == selected.ResourceId && declared.RelativePath == selected.RelativePath))) throw Invalid();
            _definitionDigest = Digest(BoundedJson.Serialize(new { binding, script }));
            var installed = new ExtensionInstalledEvidence(instance, database, contracts, roots).ReadCurrent(selection.ExtensionId)
                ?? throw Invalid();
            if (!installed.Metadata.IsInstalled || installed.Metadata.Revision != selection.ExpectedInstalledRevision ||
                installed.Metadata.Package.Kind is not (ExtensionPackageKind.DataSpecialized or ExtensionPackageKind.ManagedCode)) throw Invalid();
            Automation.SpecializedScriptPolicy.Require(script, ExtensionDetailsReader.ReadListing(installed, contracts));
            _installed = installed.Metadata;
            var configurations = new List<ConfigurationRevision>();
            var inputs = ImmutableArray.CreateBuilder<TaskDefinitionsResource>();
            var stamps = ImmutableArray.CreateBuilder<AdapterResourceStamp>();
            var blobs = new ImmutableBlobStore(instance, contracts);
            long total = 0;
            foreach (var resource in selection.Resources)
            {
                RequireLive();
                var source = ReadResource(resource, binding, script); configurations.Add(source);
                var entry = source.Tree.Entries.SingleOrDefault(item => item.RelativePath == resource.RelativePath && item.Kind == FileObjectKind.File)
                    ?? throw Invalid();
                total = checked(total + entry.SizeBytes);
                if (entry.SizeBytes > 1048576 || total > 1048576) throw new IOException("adapter_resource_input_limit");
                var input = blobs.OpenVerified(new($"blobs/content/{entry.Sha256}.bin", entry.SizeBytes, entry.Sha256!));
                _held.Add(input);
                var bytes = new byte[checked((int)entry.SizeBytes)]; input.ReadExactly(bytes);
                var declaration = script.AdapterResources.Single(item => item.ResourceId == resource.ResourceId);
                var target = script.ConfigurationPaths[declaration.DeclarationIndex];
                inputs.Add(TaskResourceValue.Create(selection.OperationId, resource.ResourceId, source.Revision,
                    entry.Sha256!, bytes, declaration.Syntax) with { RelativePath = declaration.RelativePath,
                    SourcePath = target.RootId == script.RootId ? string.Join("/", new[] { target.RelativePath, declaration.RelativePath }
                        .Where(part => part.Length != 0)) : null });
                stamps.Add(new(resource.ResourceId, source.Revision, resource.RelativePath, entry.Sha256!));
            }
            Resources = inputs.ToImmutable(); Configurations = configurations.AsReadOnly();
            Stamp = new(binding.Id, binding.Revision, script.Revision, selection.ExtensionId,
                installed.Metadata.Revision, installed.Metadata.Package.ManifestSha256, stamps.ToImmutable());
            RequireCurrent();
        }
        catch { foreach (var held in _held.AsEnumerable().Reverse()) held.Dispose(); _rootOwnership.Dispose(); throw; }
    }

    internal void RequireCurrent()
    {
        RequireLive(); _source?.RequireCurrent();
        var (binding, script) = ReadDefinitions();
        if (Digest(BoundedJson.Serialize(new { binding, script })) != _definitionDigest) throw Invalid();
        var installed = new ExtensionInstalledEvidence(_instance, _database, _contracts, _roots).ReadCurrent(Selection.ExtensionId);
        if (installed?.Metadata != _installed) throw Invalid();
        foreach (var selected in Selection.Resources)
        {
            var source = ReadResource(selected, binding, script);
            var expected = Stamp.Resources.Single(resource => resource.ResourceId == selected.ResourceId);
            var entry = source.Tree.Entries.SingleOrDefault(item => item.RelativePath == selected.RelativePath && item.Kind == FileObjectKind.File);
            if (entry?.Sha256 != expected.Sha256 || source.Revision != expected.Revision) throw Invalid();
        }
    }

    internal Func<string, string>? ReadSources(TaskAdapterExecutor executor, CancellationToken cancellation)
    {
        if (!executor.ReadsSources) return null;
        _source ??= new(_instance, _database, _lease, _manager, Selection.BindingId, Selection.ExpectedBindingRevision,
            Selection.ExtensionId, RequireLive, Root);
        if (!_held.Contains(_source)) _held.Add(_source);
        return relative =>
        {
            if (!executor.AllowsSource(relative)) throw new UnauthorizedAccessException("adapter_source_outside_declaration");
            var file = _source.ReadMetadata(relative, cancellation);
            return file is null ? "null" : StrictJson.ReadSource(file.Content);
        };
    }

    internal IReadOnlyList<ExecutionAdapterSourceFile>? FreezeSources(CancellationToken cancellation)
    {
        if (_source is null) return null;
        var frozen = _source.Freeze(cancellation);
        return frozen.Files.Select(file =>
        {
            var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(_source.Root.Path, file.Path.Replace('/', Path.DirectorySeparatorChar)));
            return new ExecutionAdapterSourceFile
            {
                RelativePath = file.Path, Path = path, Identity = FileTreeProtocol.ToWire(file.Identity),
                ParentIdentity = FileTreeProtocol.ToWire(PhysicalObjectIdentity.Read(Path.GetDirectoryName(path)!)),
                Content = new() { RelativePath = file.Content.RelativePath, SizeBytes = file.Content.SizeBytes, Sha256 = file.Content.Sha256 }
            };
        }).ToArray();
    }

    internal void RequireLive()
    {
        ObjectDisposedException.ThrowIf(_disposed, this); _lease.VerifyHeld(_instance); _requireCaller();
        if (!Root.IsActive) throw Invalid();
    }
    internal void BindCompilation(string fingerprint)
    { RequireLive(); if (!FileTreePolicy.Digest(fingerprint)) throw Invalid(); Stamp = Stamp with { CompilationFingerprint = fingerprint }; }

    private (AutomationBinding Binding, AutomationScript Script) ReadDefinitions()
    {
        var catalog = new AutomationDefinitionStore(_database).Read();
        var binding = catalog.Bindings.SingleOrDefault(item => item.Id == Selection.BindingId) ?? throw Invalid();
        var script = catalog.Scripts.SingleOrDefault(item => item.Id == binding.ScriptId) ?? throw Invalid();
        if (binding.Revision != Selection.ExpectedBindingRevision || script.Kind != AutomationKind.Specialized ||
            script.ExtensionId != Selection.ExtensionId || !catalog.Users.Any(user => user.Id == binding.UserId)) throw Invalid();
        return (binding, script);
    }

    private ConfigurationRevision ReadResource(AdapterResourceSelection resource, AutomationBinding binding, AutomationScript script)
    {
        var declaration = script.AdapterResources.SingleOrDefault(item => item.ResourceId == resource.ResourceId && item.RelativePath == resource.RelativePath) ?? throw Invalid();
        var source = AdapterConfigurationPolicy.RequireSaved(binding, script, declaration, new(_instance, _database, _contracts));
        if (source.Revision != resource.ExpectedRevision || source.ScriptId != script.Id || source.UserId != binding.UserId) throw Invalid();
        return source;
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        try { foreach (var held in _held.AsEnumerable().Reverse()) held.Dispose(); }
        finally { _rootOwnership.Dispose(); }
    }

    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static IOException Invalid() => new("adapter_selected_source_changed");
}
