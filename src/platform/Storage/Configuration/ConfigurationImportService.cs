using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Extensions;

namespace NexusPipeline.Platform.Storage.Configuration;

public sealed record ConfigurationImportResource(string ResourceId, long ExpectedRevision);
public sealed record ConfigurationImportRequest(Guid OperationId, Guid BindingId, long ExpectedBindingRevision,
    long ExpectedScriptRevision, ImmutableArray<ConfigurationImportResource> Resources)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? CandidateId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public long? ExpectedInstalledRevision { get; init; }
}

[SupportedOSPlatform("windows")]
public sealed class ConfigurationImportService(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
    InstanceLease instanceLease, ResourceLeaseManager resources, IBatchPersistenceObserver? observer = null,
    ExecutionProviderRegistry? providers = null)
{
    public ConfigurationBatchResult Import(string subject, ConfigurationImportRequest request, DateTimeOffset acceptedAt,
        CancellationToken cancellation = default, bool validateGenericContent = false,
        BindingScriptSource? selectedSource = null, Action? requireSource = null)
    {
        instanceLease.VerifyHeld(instance);
        request = Freeze(request);
        var digest = Convert.ToHexStringLower(SHA256.HashData(BoundedJson.Serialize(request)));
        var batches = new ConfigurationBatchStore(instance, database, contracts, observer);
        var replay = ReadAccepted(subject, request);
        if (replay is not null) return replay;
        using var definitions = Acquire(Guid.NewGuid(), [new("automation:definitions", ResourceClaimMode.Reference),
            new("blobs:content", ResourceClaimMode.Reference)]);
        var held = new List<IDisposable>();
        try
        {
            if ((request.CandidateId is null) != (selectedSource is null) || selectedSource is not null && (requireSource is null || selectedSource.CandidateId != request.CandidateId))
                throw new ConfigurationException("configuration_source_selection_required");
            requireSource?.Invoke();
            var resolved = Resolve(request, held, selectedSource); var targets = resolved.Targets;
            ConfigurationTargetPaths.Validate(instance, targets);
            foreach (var target in targets) held.Add(StableDirectoryLease.Acquire(Path.GetDirectoryName(target.TargetPath)!));
            var physical = PhysicalResourceAdmission.Merge(targets.SelectMany(target =>
                PhysicalResourceAdmission.Read(target.TargetPath, ResourceClaimMode.Reference)));
            using var ownership = Acquire(request.OperationId, PhysicalResourceAdmission.Merge(physical.Concat(targets.Select(target =>
                new ResourceClaim(ConfigurationRevisionService.ResourceKey(target.Address), ResourceClaimMode.Mutation)))
                .Concat(selectedSource is null ? [] : new[] { new ResourceClaim(ConfigurationRevisionService.ResourceKey(new(request.BindingId, BindingConfigurationSource.ResourceId)), ResourceClaimMode.Mutation) })
                .Append(new($"configuration:script:{resolved.ScriptId:D}", ResourceClaimMode.Reference))));
            var archives = Capture(targets, resolved.RequiredFiles, held, cancellation);
            if (validateGenericContent)
                for (var index = 0; index < targets.Length; index++)
                {
                    var tree = archives[index].Snapshot;
                    if (tree.Entries.Where(entry => entry.Kind == FileObjectKind.File).All(entry => entry.SizeBytes == 0))
                        throw new ConfigurationException("configuration_import_source_empty");
                    var documents = tree.Entries.Where(entry => entry.Kind == FileObjectKind.File).Select(entry =>
                        (Entry: entry, Syntax: Path.GetExtension(entry.RelativePath.Length == 0 ? targets[index].TargetPath : entry.RelativePath).ToLowerInvariant() switch
                        { ".json" => ConfigurationSyntax.Json, ".jsonc" => ConfigurationSyntax.JsonWithComments,
                            ".yaml" or ".yml" => ConfigurationSyntax.Yaml, _ => ConfigurationSyntax.Opaque }))
                        .Where(item => item.Syntax != ConfigurationSyntax.Opaque)
                        .Select(item => new EditDocumentRule(item.Entry.RelativePath, item.Syntax, true)).ToImmutableArray();
                    new ConfigurationContentValidation(new(instance, contracts)).Require(request.OperationId, targets[index].Address, 1,
                        new([""], [tree.Shape]) { Documents = documents }, tree);
                }
            var changes = targets.Select((target, index) => new ConfigurationRevisionChange(
                target.Address, target.ExpectedRevision, request.ExpectedBindingRevision, target.Contract,
                archives[index].Snapshot, ConfigurationUpdateKind.Manual) { SourceRequestDigest = digest }).ToImmutableArray();
            if (selectedSource is not null) changes = changes.Add(BindingConfigurationSource.Change(instance, contracts,
                resolved.Binding, resolved.BaseScript, selectedSource, digest));
            var batch = RevisionBatchPolicy.Freeze(new(changes));
            using var prepared = batches.Prepare(batch);
            RequireCurrent(targets, archives, cancellation);
            if (!physical.SequenceEqual(PhysicalResourceAdmission.Merge(targets.SelectMany(target =>
                PhysicalResourceAdmission.Read(target.TargetPath, ResourceClaimMode.Reference)))))
                throw new IOException("configuration_import_source_changed");
            cancellation.ThrowIfCancellationRequested();
            instanceLease.VerifyHeld(instance);
            void RequireCurrentProvider() { requireSource?.Invoke(); if (resolved.Provider is not null) _ = resolved.Provider.Use(_ => true); }
            return batches.CommitChecked(ConfigurationBatchStore.Identity(request.OperationId, subject, batch), prepared, [],
                acceptedAt, RequireCurrentProvider);
        }
        finally { for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose(); }
    }

    internal ConfigurationBatchResult? ReadAccepted(string subject, ConfigurationImportRequest request)
    {
        request = Freeze(request);
        var digest = Convert.ToHexStringLower(SHA256.HashData(BoundedJson.Serialize(request)));
        var inputs = request.Resources.Select(item => (Address: new ConfigurationAddress(request.BindingId, item.ResourceId), item.ExpectedRevision, Digest: digest)).ToList();
        if (request.CandidateId is not null) inputs.Add((new(request.BindingId, BindingConfigurationSource.ResourceId), 0, digest));
        return new ConfigurationBatchStore(instance, database, contracts, observer).LookupImported(request.OperationId, subject,
            request.ExpectedBindingRevision, inputs.OrderBy(item => item.Address.ResourceId, StringComparer.Ordinal).ToArray());
    }

    private sealed record BoundImport(Guid ScriptId, AutomationBinding Binding, AutomationScript BaseScript, ConfigurationTarget[] Targets, IReadOnlyDictionary<string, string> RequiredFiles,
        ExecutionProviderRegistry.PreparedExecutionProvider? Provider);

    private BoundImport Resolve(ConfigurationImportRequest request, List<IDisposable> held, BindingScriptSource? selectedSource)
    {
        var catalog = new AutomationDefinitionStore(database).Read();
        var binding = catalog.Bindings.SingleOrDefault(item => item.Id == request.BindingId)
            ?? throw new ConfigurationException("configuration_binding_missing");
        var baseScript = catalog.Scripts.Single(item => item.Id == binding.ScriptId);
        if (selectedSource is not null && binding.Source is not null) throw new ConfigurationException("configuration_source_already_saved");
        var script = BindingScriptSource.Resolve(baseScript, selectedSource is null ? binding : binding with { Source = selectedSource });
        if (binding.Revision != request.ExpectedBindingRevision || script.Revision != request.ExpectedScriptRevision)
            throw new ConfigurationException("configuration_binding_revision_conflict");
        var revisions = new ConfigurationRevisionStore(instance, database, contracts);
        var required = new Dictionary<string, string>(StringComparer.Ordinal);
        var provider = ProviderConfigurationPolicy.Acquire(script, providers);
        if (provider is not null) held.Add(provider);
        var declarations = provider is null ? [] : ProviderConfigurationPolicy.Declarations(script, provider);
        var targets = request.Resources.Select(selected =>
        {
            var declaration = provider is null ? null : declarations.SingleOrDefault(item => item.ResourceId == selected.ResourceId)
                ?? throw new ConfigurationException("configuration_provider_resource_unbound");
            var adapter = script.Kind == AutomationKind.Specialized && provider is null
                ? script.AdapterResources.SingleOrDefault(item => item.ResourceId == selected.ResourceId)
                    ?? throw new ConfigurationException("configuration_adapter_resource_missing") : null;
            var path = declaration is not null ? script.ConfigurationPaths[declaration.DeclarationIndex] : adapter is null ? script.ConfigurationPaths.SingleOrDefault(item =>
                GenericConfigurationPolicy.ResourceId(item) == selected.ResourceId)
                    ?? throw new ConfigurationException("configuration_resource_missing") : script.ConfigurationPaths[adapter.DeclarationIndex];
            var address = new ConfigurationAddress(binding.Id, selected.ResourceId);
            if ((revisions.Read(address)?.Revision ?? 0) != selected.ExpectedRevision)
                throw new ConfigurationException("configuration_revision_conflict");
            if (declaration is not null && selected.ExpectedRevision > 0)
                _ = ProviderConfigurationPolicy.RequireSaved(binding, script, declaration, provider!, revisions);
            var root = catalog.Roots.Single(item => item.Id == path.RootId);
            var parent = PhysicalPathPolicy.NormalizeWithoutLinks(root.Path);
            if (!AutomationCatalogPolicy.RelativePath(path.RelativePath)) throw new ConfigurationException("configuration_import_path_invalid");
            var target = ConfigurationTargetPaths.Canonical(PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(parent, path.RelativePath)));
            if (!target.Equals(parent, StringComparison.OrdinalIgnoreCase) &&
                !target.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ConfigurationException("configuration_import_path_invalid");
            if (adapter is not null) required.Add(selected.ResourceId, adapter.RelativePath);
            return new ConfigurationTarget(address, selected.ExpectedRevision, declaration is not null ?
                provider!.Use(package => ProviderConfigurationPolicy.Contract(provider.ExtensionId, package.Declaration.EntryId, declaration)) : adapter is null ? GenericConfigurationPolicy.Contract(path) :
                AdapterConfigurationPolicy.Contract(script, adapter), target);
        }).ToArray();
        return new(script.Id, binding, baseScript, targets, required, provider);
    }

    private ArchivedFileTree[] Capture(ConfigurationTarget[] targets, IReadOnlyDictionary<string, string> required,
        List<IDisposable> held, CancellationToken cancellation)
    {
        var archives = new List<ArchivedFileTree>();
        var archiver = new FileTreeArchive(new(SnapshotBudget.Default), new(instance, contracts));
        long bytes = 0; var entries = 0;
        foreach (var target in targets)
        {
            var archive = archiver.Capture(target.TargetPath, cancellation);
            if (archive.Snapshot.Shape == FileObjectKind.Missing || required.TryGetValue(target.Address.ResourceId, out var file) &&
                !archive.Snapshot.Entries.Any(entry => entry.Kind == FileObjectKind.File && entry.RelativePath == file))
                throw new ConfigurationException("configuration_import_source_missing");
            bytes = checked(bytes + archive.Snapshot.Entries.Sum(entry => entry.SizeBytes)); entries += archive.Snapshot.Entries.Count;
            if (bytes > SnapshotBudget.Default.MaximumTotalBytes || entries > SnapshotBudget.Default.MaximumEntries)
                throw new ConfigurationException("configuration_import_limit");
            held.Add(TreeReadLease.Acquire(target.TargetPath, PhysicalObjectIdentity.Read(Path.GetDirectoryName(target.TargetPath)!), archive));
            archives.Add(archive);
        }
        return archives.ToArray();
    }

    private static void RequireCurrent(ConfigurationTarget[] targets, ArchivedFileTree[] archives, CancellationToken cancellation)
    {
        var scanner = new FileTreeScanner(SnapshotBudget.Default);
        for (var index = 0; index < targets.Length; index++)
        {
            var actual = scanner.Capture(targets[index].TargetPath, cancellation);
            if (actual.Shape != archives[index].Snapshot.Shape || !actual.Entries.SequenceEqual(archives[index].Snapshot.Entries) ||
                !PhysicalTreeIdentity.Capture(targets[index].TargetPath, actual).SequenceEqual(archives[index].OriginalObjects))
                throw new IOException("configuration_import_source_changed");
        }
    }

    private ResourceOperationLease Acquire(Guid operationId, IReadOnlyList<ResourceClaim> claims)
    {
        var admission = resources.TryAcquire(operationId, claims);
        return admission.Lease ?? throw new ConfigurationException(admission.Denial == ResourceLeaseDenial.Conflict
            ? "configuration_resource_conflict" : "configuration_host_unavailable");
    }

    private static ConfigurationImportRequest Freeze(ConfigurationImportRequest request)
    {
        if (request is null || request.OperationId == Guid.Empty || request.BindingId == Guid.Empty ||
            request.ExpectedBindingRevision is < 1 or > 9007199254740991 || request.ExpectedScriptRevision is < 1 or > 9007199254740991 ||
            request.Resources.IsDefaultOrEmpty || request.Resources.Length > 32 || request.Resources.Any(item => item is null ||
                string.IsNullOrWhiteSpace(item.ResourceId) || item.ResourceId.Length > 120 || item.ResourceId.Any(char.IsControl) ||
                item.ExpectedRevision is < 0 or >= 9007199254740991) ||
            request.Resources.Select(item => item.ResourceId).Distinct(StringComparer.Ordinal).Count() != request.Resources.Length)
            throw new ConfigurationException("configuration_import_request_invalid");
        if (request.CandidateId is not null && (string.IsNullOrWhiteSpace(request.CandidateId) || request.CandidateId.Length > 1024 ||
            request.CandidateId.Any(char.IsControl) || request.ExpectedInstalledRevision is not > 0 ||
            request.Resources.Length > 31 || request.Resources.Any(item => item.ExpectedRevision != 0)) ||
            request.CandidateId is null && request.ExpectedInstalledRevision is not null)
            throw new ConfigurationException("configuration_import_request_invalid");
        return request with { Resources = request.Resources.OrderBy(item => item.ResourceId, StringComparer.Ordinal).ToImmutableArray() };
    }
}
