using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Settings;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

public sealed record LocalExtensionCandidate(string CandidateId, string ExtensionId, string Name, string Summary,
    string Kind, string PackageVersion, string MinHostVersion, string RelativePath, IReadOnlyList<string> Capabilities,
    string State, string? Reason)
{
    public string? GameName { get; init; }
    public string? DiscoveryId { get; init; }
    internal ExtensionCatalogIdentity? UpdateCatalog { get; init; }
}
public sealed record LocalExtensionNotice(Guid BatchId, IReadOnlyList<string> Removed,
    IReadOnlyList<string> Waiting, IReadOnlyList<string> Installed);
public sealed record LocalExtensionSnapshot(IReadOnlyList<LocalExtensionCandidate> Items, LocalExtensionNotice? Notice, bool Scanning);
public sealed record LocalExtensionApply(Guid OperationId, string Status);

[SupportedOSPlatform("windows")]
internal sealed class LocalExtensionIntake(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
    PayloadApplyRoots roots, ExtensionRuntimeLifecycle runtime, ExtensionApplyWorkflow workflow,
    Func<string, ExtensionStoreChannel, CancellationToken, Task<ExtensionCatalogView>> refresh,
    Func<string, ExtensionStoreChannel, Task> installBundle, SemaphoreSlim mutation, CancellationToken stopping)
{
    private readonly object _gate = new();
    private readonly Guid _batch = Guid.NewGuid();
    private readonly HashSet<string> _installed = new(StringComparer.Ordinal);
    private LocalExtensionCandidate[] _items = [];
    private Dictionary<string, LocalSelection> _selections = new(StringComparer.Ordinal);
    private Task<LocalExtensionSnapshot>? _scan;
    private bool _starting = true;

    internal LocalExtensionSnapshot Read() { lock (_gate) return Snapshot(); }

    private LocalExtensionSnapshot Snapshot()
    {
        var (removed, waiting) = runtime.ReadExternalRemovals();
        var notice = removed.Length + waiting.Length + _installed.Count == 0 ? null :
            new LocalExtensionNotice(_batch, removed, waiting, _installed.Order(StringComparer.Ordinal).ToArray());
        return new(_items, notice, _starting || _scan is { IsCompleted: false });
    }

    internal Task<LocalExtensionSnapshot> ScanAsync()
    {
        lock (_gate)
        {
            _starting = false;
            return _scan is { IsCompleted: false } ? _scan : _scan = Task.Run(ScanCoreAsync, CancellationToken.None);
        }
    }

    internal async Task WaitStoppedAsync()
    {
        Task? scan;
        lock (_gate) scan = _scan;
        if (scan is not null)
            try { await scan.ConfigureAwait(false); }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        await mutation.WaitAsync().ConfigureAwait(false);
        mutation.Release();
    }

    internal async Task RunMaintenanceWhenIdleAsync(Func<Task> maintenance, CancellationToken cancellation)
    {
        if (!await mutation.WaitAsync(0, cancellation).ConfigureAwait(false)) return;
        try { await maintenance().ConfigureAwait(false); }
        finally { mutation.Release(); }
    }

    private async Task<LocalExtensionSnapshot> ScanCoreAsync()
    {
        await mutation.WaitAsync(stopping).ConfigureAwait(false);
        try
        {
            await runtime.ReconcileFilesAsync(stopping).ConfigureAwait(false);
            var inventory = FindInputs();
            var catalogs = await ReadCatalogsAsync(inventory).ConfigureAwait(false);
            var views = new List<LocalExtensionCandidate>();
            foreach (var input in inventory.Inputs)
            {
                stopping.ThrowIfCancellationRequested();
                await InspectAsync(input, inventory.Inputs, catalogs, views).ConfigureAwait(false);
            }
            views.AddRange(inventory.Invalid);
            lock (_gate)
            {
                var revision = new HostSettingsStore(database).Read().Revision;
                _selections = new(StringComparer.Ordinal);
                _items = views.Select(item =>
                {
                    if (item.State is not ("pendingApproval" or "pendingUpdate")) return item;
                    var installedRevision = Original(item.ExtensionId)?.Metadata.Revision ?? 0;
                    var frozen = new LocalSelection(item.CandidateId, installedRevision, revision, item.UpdateCatalog);
                    var digest = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(frozen)));
                    _selections.Add(digest, frozen);
                    return item with { CandidateId = digest, DiscoveryId = item.CandidateId };
                }).ToArray();
                return Snapshot() with { Scanning = false };
            }
        }
        finally { mutation.Release(); }
    }

    private Inventory FindInputs()
    {
        var installed = ExtensionInstalledRows.ReadIds(database, installedOnly: true).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var inputs = new List<LocalExtensionInput>();
        var invalid = new List<LocalExtensionCandidate>();
        var bundles = new List<string>();
        var root = PhysicalPathPolicy.NormalizeWithoutLinks(roots.ExtensionPayloadRoot);
        using var lease = StableDirectoryLease.Acquire(root);
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            stopping.ThrowIfCancellationRequested();
            if (inputs.Count + invalid.Count + bundles.Count == 256) break;
            var name = Path.GetFileName(path);
            if (name.StartsWith('.') || installed.Contains(name)) continue;
            try
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                var folder = (attributes & FileAttributes.Directory) != 0;
                if (folder && File.Exists(Path.Combine(path, "index.json")) && File.Exists(Path.Combine(path, "catalog.json")) &&
                    File.Exists(Path.Combine(path, "catalog.sig.json")))
                { bundles.Add(path); continue; }
                if (folder ? !File.Exists(Path.Combine(path, "manifest.json")) :
                    !Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                inputs.Add(LocalExtensionInput.Read(path, contracts, stopping));
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
            catch (Exception error) when (InvalidInput(error))
            { invalid.Add(InvalidCandidate(name, error.Message == "extension_host_version_unsupported" ? error.Message : "extension_local_package_invalid")); }
        }
        inputs.Sort((left, right) =>
        {
            var shape = (right.Tree is not null).CompareTo(left.Tree is not null);
            return shape != 0 ? shape : StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path);
        });
        return new(inputs, invalid, bundles);
    }

    private async Task<VerifiedExtensionCatalog[]> ReadCatalogsAsync(Inventory inventory)
    {
        if (runtime.Catalog is null)
        {
            inventory.Invalid.AddRange(inventory.Bundles.Select(path =>
                InvalidCandidate(Path.GetFileName(path), "extension_release_trust_unprovisioned")));
            return [];
        }
        var channel = new HostSettingsStore(database).Read().Values.Updates.PluginSource == PluginSourceChannel.Develop
            ? ExtensionStoreChannel.Develop : ExtensionStoreChannel.Stable;
        foreach (var bundle in inventory.Bundles)
        {
            try { await ImportBundleAsync(bundle).ConfigureAwait(false); }
            catch (Exception error) when (InvalidInput(error))
            { inventory.Invalid.Add(InvalidCandidate(Path.GetFileName(bundle), "extension_local_package_invalid")); }
        }
        if (inventory.Inputs.Count == 0) return [];
        var current = await ReadAsync().ConfigureAwait(false);
        if (inventory.Inputs.Any(input => !LocalExtensionEvidence.ValidId(input.Manifest.ExtensionId) &&
            !current.Any(saved => saved.Entries.Any(entry => Match(input, entry)))))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try { _ = await refresh("host:local-extension-intake", channel, timeout.Token).ConfigureAwait(false); }
            catch (Exception error) when (!stopping.IsCancellationRequested &&
                (InvalidInput(error) || error is HttpRequestException or OperationCanceledException)) { }
            current = await ReadAsync().ConfigureAwait(false);
        }
        return current;

        async Task<VerifiedExtensionCatalog[]> ReadAsync()
        {
            var catalogs = new List<VerifiedExtensionCatalog>();
            foreach (var value in new[] { channel, channel == ExtensionStoreChannel.Stable
                ? ExtensionStoreChannel.Develop : ExtensionStoreChannel.Stable })
                if (await runtime.Catalog.ReadCachedAsync(value, stopping).ConfigureAwait(false) is { } verified)
                    catalogs.Add(verified);
            return catalogs.ToArray();
        }
    }

    private async Task ImportBundleAsync(string path)
    {
        var parent = PhysicalObjectIdentity.Read(roots.ExtensionPayloadRoot);
        var identity = PhysicalObjectIdentity.Read(path);
        using var owned = InstalledDirectoryMoveHandle.Acquire(path, identity);
        var before = ExtensionInstalledRows.ReadIds(database, installedOnly: true).ToHashSet(StringComparer.Ordinal);
        using (TreeReadLease.Capture(path, parent, new(SnapshotBudget.Default), stopping, "extension.local.bundle", out _))
        {
            var pointer = Path.Combine(path, "index.json");
            var info = new FileInfo(pointer);
            if (info.Length is < 1 or > StrictJson.MaximumBytes) throw new IOException("extension_bundle_metadata_size_invalid");
            using var index = StrictJson.Parse(File.ReadAllBytes(pointer));
            if (index.RootElement.ValueKind != JsonValueKind.Object ||
                !index.RootElement.TryGetProperty("channel", out var value) || value.ValueKind != JsonValueKind.String)
                throw new IOException("extension_channel_invalid");
            var channel = value.GetString() switch
            {
                "stable" => ExtensionStoreChannel.Stable,
                "develop" => ExtensionStoreChannel.Develop,
                _ => throw new IOException("extension_channel_invalid")
            };
            await installBundle(path, channel).ConfigureAwait(false);
        }
        MoveProcessed(path, parent, identity, FileObjectKind.Directory, owned.Handle);
        foreach (var id in ExtensionInstalledRows.ReadIds(database, installedOnly: true).Where(id => !before.Contains(id)))
        {
            var record = Original(id);
            var name = record?.Operation.Source.Package?.Entry.Metadata?.DisplayName ?? id;
            lock (_gate) _installed.Add(name);
        }
    }

    private async Task InspectAsync(LocalExtensionInput input, IReadOnlyList<LocalExtensionInput> inputs,
        IReadOnlyList<VerifiedExtensionCatalog> catalogs, List<LocalExtensionCandidate> views)
    {
        var installed = Original(input.Manifest.ExtensionId);
        if (installed?.Metadata.IsInstalled == true && SameInstalledContent(input, installed))
        { ArchiveProcessed(input); return; }
        var official = catalogs.SelectMany(catalog => catalog.Entries.Where(entry => entry.ExtensionId == input.Manifest.ExtensionId)
            .Select(entry => (Catalog: catalog, Entry: entry))).ToArray();
        if (official.FirstOrDefault(item => Match(input, item.Entry)) is { Catalog: not null } selection)
        {
            if (installed?.Metadata.IsInstalled == true)
            {
                if (CanUpdate(installed, selection.Entry, selection.Catalog.Identity))
                    views.Add(View(input, "pendingUpdate", null) with { UpdateCatalog = selection.Catalog.Identity });
                else views.Add(View(input, "unavailable", "extension_local_id_conflict"));
                return;
            }
            var paired = FindArchive(input, selection.Entry, inputs);
            var source = paired is null ? input : input with { ArchivePath = paired.Path };
            try
            {
                var receipt = await ApplyAsync(source, selection.Catalog.Identity, selection.Entry, null,
                    Guid.NewGuid(), "host:local-extension-intake", installed).ConfigureAwait(false);
                if (receipt.Status != "installed") throw new IOException("extension_local_apply_failed");
                lock (_gate) _installed.Add(input.Manifest.DisplayName);
                ArchiveProcessed(input);
                if (paired is not null) ArchiveProcessed(paired);
                return;
            }
            catch (Exception error) when (InvalidInput(error) || error is HttpRequestException)
            {
                var reason = input.Tree is not null ? "extension_local_official_verification_required" : "extension_local_apply_failed";
                views.Add(View(input, input.Tree is not null ? "awaitingVerification" : "unavailable", reason));
                return;
            }
        }
        if (official.Length != 0 || RegisteredOfficial(installed))
        {
            views.Add(View(input, input.Tree is null ? "unavailable" : "awaitingVerification",
                input.Tree is null ? "extension_local_package_invalid" : "extension_local_official_verification_required"));
            return;
        }
        if (!LocalExtensionEvidence.ValidId(input.Manifest.ExtensionId))
        { views.Add(View(input, "unavailable", "extension_local_source_unverified")); return; }
        if (installed?.Metadata.IsInstalled == true)
        {
            if (!CanUpdate(installed, input.Entry, null))
            { views.Add(View(input, "unavailable", "extension_local_id_conflict")); return; }
        }
        views.Add(View(input, "pendingApproval", null));
    }

    internal async Task<LocalExtensionApply> EnableAsync(string id, Guid operation, string subject)
    {
        if (!LocalExtensionEvidence.IsDigest(id) || operation == Guid.Empty) throw new ArgumentException("extension_local_candidate_changed");
        await mutation.WaitAsync(stopping).ConfigureAwait(false);
        try
        {
            var previous = new ExtensionQueryService(instance, runtime.InstanceLease, database, contracts, roots).Read(operation, subject);
            if (previous is not null)
            {
                var binding = new ExtensionPayloadBinding(instance, database, contracts, roots).Read(previous.Operation.InitialJournalId!.Value);
                if (binding.Source.Package?.LocalApproval != id && binding.Source.LocalUpdateConfirmation != id)
                    throw new IOException("extension_local_candidate_changed");
                return previous.Outcome switch
                {
                    ExtensionApplyOutcome.Installed => new(operation, "installed"),
                    ExtensionApplyOutcome.Pending => new(operation, "pending"),
                    _ => throw new IOException("extension_local_apply_failed")
                };
            }
            LocalSelection? shown;
            lock (_gate) _selections.TryGetValue(id, out shown);
            if (shown is null) throw new IOException("extension_local_candidate_changed");
            var inputs = FindInputs().Inputs;
            var input = inputs.SingleOrDefault(item => item.CandidateId == shown.InputDigest) ??
                throw new IOException("extension_local_candidate_changed");
            var installed = Original(input.Manifest.ExtensionId);
            if ((installed?.Metadata.Revision ?? 0) != shown.InstallationRevision ||
                new HostSettingsStore(database).Read().Revision != shown.SettingsRevision)
                throw new IOException("extension_local_candidate_changed");
            LocalExtensionApply receipt;
            LocalExtensionInput? paired = null;
            if (shown.UpdateCatalog is { } expected)
            {
                var catalog = runtime.Catalog is { } store
                    ? await store.ReadCachedAsync(expected.Channel, stopping).ConfigureAwait(false) : null;
                var entry = catalog?.Entries.SingleOrDefault(item => Match(input, item));
                if (catalog?.Identity != expected || entry is null || installed?.Metadata.IsInstalled != true ||
                    !CanUpdate(installed, entry, expected)) throw new IOException("extension_local_candidate_changed");
                paired = FindArchive(input, entry, inputs);
                var source = paired is null ? input : input with { ArchivePath = paired.Path };
                receipt = await ApplyAsync(source, expected, entry, id, operation, subject, installed, shown.SettingsRevision).ConfigureAwait(false);
            }
            else
            {
                if (!LocalExtensionEvidence.ValidId(input.Manifest.ExtensionId) || RegisteredOfficial(installed) ||
                    await CatalogContainsAsync(input.Manifest.ExtensionId).ConfigureAwait(false) ||
                    installed?.Metadata.IsInstalled == true && !CanUpdate(installed, input.Entry, null))
                    throw new IOException("extension_local_id_conflict");
                using var draft = input.Tree is null ? null : input.MakeArchive(instance, stopping);
                var source = draft is null ? input : input with { ArchivePath = draft.Path };
                var entry = draft is null ? input.Entry : LocalExtensionInput.ReadZip(draft.Path, contracts, stopping).Entry;
                receipt = await ApplyAsync(source, null, entry, id, operation, subject, installed, shown.SettingsRevision).ConfigureAwait(false);
            }
            if (receipt.Status == "installed")
            {
                ArchiveProcessed(input);
                if (paired is not null) ArchiveProcessed(paired);
                lock (_gate)
                {
                    _items = _items.Where(item => item.CandidateId != id).ToArray();
                    _selections.Remove(id);
                }
            }
            return receipt;
        }
        finally { mutation.Release(); }
    }

    private async Task<LocalExtensionApply> ApplyAsync(LocalExtensionInput input, ExtensionCatalogIdentity? catalog,
        ExtensionCatalogEntry entry, string? approval, Guid operation, string subject, BoundExtensionInstallation? installed, long? settingsRevision = null)
    {
        var action = installed?.Metadata.IsInstalled == true ? PayloadAction.Upgrade : PayloadAction.Install;
        var import = input.Tree is not null && StringComparer.OrdinalIgnoreCase.Equals(input.Path,
            Path.Combine(roots.ExtensionPayloadRoot, entry.ExtensionId));
        var request = new ExtensionApplyRequest(action, entry.ExtensionId, installed?.Metadata.Revision ?? 0,
            settingsRevision ?? new HostSettingsStore(database).Read().Revision, catalog, entry)
            { LocalApproval = catalog is null ? approval : null, LocalUpdateConfirmation = catalog is null ? null : approval, ImportTarget = import };
        var accepted = false;
        try
        {
            var completion = await workflow.RunAsync(operation, subject, request, _ => accepted = true, stopping, input).ConfigureAwait(false);
            return new(operation, completion.Result?.IsInstalled == true ? "installed" : "pending");
        }
        catch (Exception error) when (accepted && !stopping.IsCancellationRequested && InvalidInput(error))
        { return new(operation, "pending"); }
    }

    private async Task<bool> CatalogContainsAsync(string id)
    {
        if (runtime.Catalog is not { } catalog) return false;
        foreach (var channel in new[] { ExtensionStoreChannel.Stable, ExtensionStoreChannel.Develop })
            if ((await catalog.ReadCachedAsync(channel, stopping).ConfigureAwait(false))?.Entries.Any(entry => entry.ExtensionId == id) == true)
                return true;
        return false;
    }

    private BoundExtensionInstallation? Original(string id) => new ExtensionInstalledEvidence(instance, database, contracts, roots).ReadCurrent(id);

    private bool RegisteredOfficial(BoundExtensionInstallation? installed)
    {
        if (installed is null) return false;
        var source = installed.Operation.Source;
        if (source.Action == PayloadAction.Remove && source.OriginalFootprint is { } reference)
        {
            var original = new InstalledFootprintCodec(instance, roots, contracts).ReadMetadata(reference);
            source = new PayloadSourceCodec(instance, contracts).ReadMetadata(original.Source);
        }
        return source.Package?.Catalog is not null;
    }

    private static bool CanUpdate(BoundExtensionInstallation installed, ExtensionCatalogEntry entry, ExtensionCatalogIdentity? catalog) =>
        (installed.Operation.Source.Package?.Catalog is not null) == (catalog is not null) &&
        installed.Metadata.Package.Kind == (entry.Kind == "managed-code" ? ExtensionPackageKind.ManagedCode : ExtensionPackageKind.DataSpecialized) &&
        ExtensionUpgradeSource.IsNewer(entry.PackageVersion, installed.Metadata.Package.PackageVersion);

    private bool SameInstalledContent(LocalExtensionInput input, BoundExtensionInstallation installed)
    {
        if (installed.Operation.Source.Package is not { } package || input.Entry.ManifestSha256 != package.Entry.ManifestSha256)
            return false;
        if (input.Tree is not { } tree) return input.Entry.ArtifactSha256 == package.Archive.Sha256;
        try
        {
            using var bytes = new ImmutableBlobStore(instance, contracts).OpenVerified(package.Archive);
            using var zip = ExtensionPackageArchive.OpenFrozen(bytes, tree.Snapshot, stopping);
            return true;
        }
        catch (Exception error) when (InvalidInput(error)) { return false; }
    }

    private void ArchiveProcessed(LocalExtensionInput input)
    {
        if (!File.Exists(input.Path) && !Directory.Exists(input.Path) ||
            StringComparer.OrdinalIgnoreCase.Equals(input.Path, Path.Combine(roots.ExtensionPayloadRoot, input.Manifest.ExtensionId))) return;
        try
        {
            var fresh = LocalExtensionInput.Read(input.Path, contracts, stopping);
            if (fresh.CandidateId != input.CandidateId) return;
            var sourceParent = PhysicalObjectIdentity.Read(roots.ExtensionPayloadRoot);
            if (input.Tree is { } tree)
            {
                using var root = InstalledDirectoryMoveHandle.Acquire(input.Path, input.ObjectIdentity);
                using (TreeReadLease.Acquire(input.Path, sourceParent, tree)) { }
                MoveProcessed(input.Path, sourceParent, input.ObjectIdentity, FileObjectKind.Directory, root.Handle);
            }
            else
            {
                using var parent = PhysicalDirectoryHandle.Open(roots.ExtensionPayloadRoot, sourceParent);
                using var file = parent.OpenOwnedFile(Path.GetFileName(input.Path));
                using var content = new FileStream(file, FileAccess.Read);
                if (input.ObjectIdentity != PhysicalObjectIdentity.Read(file) || content.Length != input.Entry.ArtifactBytes ||
                    Convert.ToHexStringLower(SHA256.HashData(content)) != input.Entry.ArtifactSha256) return;
                MoveProcessed(input.Path, sourceParent, input.ObjectIdentity, FileObjectKind.File, file);
            }
        }
        catch (Exception error) when (InvalidInput(error)) { }
    }

    private void MoveProcessed(string path, PhysicalObjectIdentity parent, PhysicalObjectIdentity identity,
        FileObjectKind kind, Microsoft.Win32.SafeHandles.SafeFileHandle owned)
    {
        var processed = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(roots.ExtensionPayloadRoot, ".processed"));
        Directory.CreateDirectory(processed);
        var destination = Path.Combine(processed, $"{Guid.NewGuid():N}-{Path.GetFileName(path)}");
        PhysicalObjectMove.MoveVerified(new(path, parent, identity, destination, PhysicalObjectIdentity.Read(processed), kind), owned);
    }

    private static LocalExtensionInput? FindArchive(LocalExtensionInput input, ExtensionCatalogEntry entry,
        IReadOnlyList<LocalExtensionInput> inputs) => input.Tree is null ? null :
        inputs.FirstOrDefault(other => other.Tree is null && Match(other, entry));

    private static bool Match(LocalExtensionInput input, ExtensionCatalogEntry entry) =>
        input.Entry.ExtensionId == entry.ExtensionId && input.Entry.PackageVersion == entry.PackageVersion &&
        input.Entry.ManifestSha256 == entry.ManifestSha256 &&
        (input.Tree is not null || input.Entry.ArtifactBytes == entry.ArtifactBytes && input.Entry.ArtifactSha256 == entry.ArtifactSha256);
    private static bool InvalidInput(Exception error) =>
        error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or
            KeyNotFoundException or System.ComponentModel.Win32Exception or System.Security.Cryptography.CryptographicException ||
            error.GetType().Namespace == "Acornima";
    private static LocalExtensionCandidate View(LocalExtensionInput input, string state, string? reason) =>
        new(input.CandidateId, input.Manifest.ExtensionId, input.Manifest.DisplayName, input.Manifest.Summary ?? "", input.Manifest.Kind,
            input.Manifest.PackageVersion, input.Manifest.MinHostVersion, @"runtime\extensions\" + Path.GetFileName(input.Path),
            input.Manifest.Capabilities, state, reason) { GameName = input.Manifest.GameName, DiscoveryId = input.CandidateId };
    private static LocalExtensionCandidate InvalidCandidate(string file, string reason)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(file)));
        return new(hash, "input-" + hash[..12], file, "", "data-specialized", "", "", @"runtime\extensions\" + file, [], "unavailable", reason) { DiscoveryId = hash };
    }

    private sealed record LocalSelection(string InputDigest, long InstallationRevision, long SettingsRevision,
        ExtensionCatalogIdentity? UpdateCatalog);
    private sealed record Inventory(List<LocalExtensionInput> Inputs, List<LocalExtensionCandidate> Invalid, List<string> Bundles);
}
