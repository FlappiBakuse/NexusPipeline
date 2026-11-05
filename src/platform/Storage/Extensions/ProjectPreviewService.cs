using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility.Compilation;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed record ProjectPreviewAsset(string Name, string Kind, long SizeBytes, string Sha256);
internal sealed record ProjectPreview(Guid PreviewId, string Fingerprint, bool CanExecute, string RootIndex,
    DateTimeOffset ExpiresAt, IReadOnlyList<ProjectPreviewAsset> Assets);
internal sealed record ProjectPreviewRead(string Name, string Sha256, string ContentBase64);

[SupportedOSPlatform("windows")]
internal sealed class ProjectPreviewService : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly InstanceLease _lease;
    private readonly ResourceLeaseManager _resources;
    private readonly ContractReader _contracts;
    private readonly ExtensionInstalledEvidence _installed;
    private readonly ExtensionActivator _activator;
    private readonly TimeProvider _clock;
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _sweep;
    private bool _disposed;
    private sealed record Entry(Guid Id, string Subject, ProjectPreviewSource Source, DateTimeOffset Expires, ResourceOperationLease Root,
        CancellationToken OwnerLifetime);

    internal ProjectPreviewService(RuntimeInstance instance, StateDatabase database, InstanceLease lease, ResourceLeaseManager resources,
        ContractReader contracts, PayloadApplyRoots roots, ExtensionActivator activator, TimeProvider? clock = null)
    {
        lease.VerifyHeld(instance); lease.RequireExtensionActivator(activator, resources);
        _instance = instance; _database = database; _lease = lease; _resources = resources; _contracts = contracts;
        _installed = new(instance, database, contracts, roots); _activator = activator; _clock = clock ?? TimeProvider.System;
        _sweep = SweepAsync();
    }

    internal ValueTask<ProjectPreview> PrepareAsync(ProjectCompilationService compilation, string subject, Guid bindingId, long bindingRevision,
        string extensionId, Action requireCaller, CancellationToken cancellation, CancellationToken ownerLifetime = default,
        JsonElement? selection = null)
    {
        RequireSubject(subject); requireCaller();
        Func<CompiledProjectSnapshot, Action, Action, ValueTask<ProjectPreview>> publish = (snapshot, current, _) =>
        {
            return ValueTask.FromResult(Publish(subject, ProjectPreviewSource.Binding(snapshot), current, ownerLifetime));
        };
        return selection is { } draft
            ? compilation.UseAsync(bindingId, bindingRevision, extensionId, draft, requireCaller, publish, cancellation)
            : compilation.UseSavedAsync(bindingId, bindingRevision, extensionId, requireCaller, publish, cancellation);
    }

    internal ValueTask<ProjectPreview> PrepareScriptAsync(ScriptProjectService compilation, string subject, Guid scriptId,
        long scriptRevision, string extensionId, JsonElement selection, Action requireCaller, CancellationToken cancellation,
        CancellationToken ownerLifetime = default)
    {
        RequireSubject(subject); requireCaller();
        return compilation.CompileAsync(scriptId, scriptRevision, extensionId, selection, requireCaller,
            (source, graph, consumer, provider, current) => ValueTask.FromResult(Publish(subject,
                ProjectPreviewSource.Script(_instance, _contracts, source, graph, consumer, provider, selection, cancellation),
                current, ownerLifetime)), cancellation);
    }

    private ProjectPreview Publish(string subject, ProjectPreviewSource source, Action current, CancellationToken ownerLifetime)
    {
        current();
        var previewIndex = source.PreviewIndex ?? throw new IOException("project_preview_undeclared");
        var names = CompilationGraph.RequirePreview(previewIndex, source.Assets.Select(item => (item.Name, item.Kind, item.References)));
        ownerLifetime.ThrowIfCancellationRequested();
        var id = Guid.NewGuid(); var root = _resources.TryAcquire(id, [new("blobs:content", ResourceClaimMode.Reference)]).Lease
            ?? throw new IOException("project_preview_unavailable");
        var entry = new Entry(id, subject, source, _clock.GetUtcNow().AddMinutes(2), root, ownerLifetime);
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this); Expire();
                foreach (var old in _entries.Values.Where(item => item.Subject == subject && item.Source.OwnerId == source.OwnerId && item.Source.IsScript == source.IsScript).ToArray())
                    Remove(old);
                if (_entries.Count >= 8) throw new IOException("project_preview_capacity");
                _entries.Add(id, entry);
            }
            current();
            return new ProjectPreview(id, source.Fingerprint, source.CanExecute, previewIndex,
                entry.Expires, Array.AsReadOnly(source.Assets.Where(item => names.Contains(item.Name)).Select(item => new ProjectPreviewAsset(item.Name,
                    item.Kind.ToString().ToLowerInvariant(), item.Content.SizeBytes, item.Content.Sha256)).ToArray()));
        }
        catch { lock (_gate) Remove(entry); root.Dispose(); throw; }
    }

    internal ProjectPreviewRead Read(string subject, Guid previewId, string assetName, Action requireCaller)
    {
        RequireSubject(subject); requireCaller(); Entry entry; ResourceOperationBorrow borrow;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); Expire();
            entry = _entries.GetValueOrDefault(previewId) ?? throw Unavailable();
            if (entry.Subject != subject) throw new UnauthorizedAccessException("project_preview_subject_unbound");
            borrow = entry.Root.Borrow(_resources, entry.Id, entry.Root.Claims);
        }
        using (borrow)
        {
            Current(entry, requireCaller);
            var names = CompilationGraph.RequirePreview(entry.Source.PreviewIndex,
                entry.Source.Assets.Select(item => (item.Name, item.Kind, item.References)));
            if (!names.Contains(assetName)) throw new UnauthorizedAccessException("project_preview_asset_forbidden");
            var asset = entry.Source.Assets.SingleOrDefault(item => item.Name == assetName) ?? throw Unavailable();
            if (asset.Kind == CompiledAssetKind.Artifact) throw new UnauthorizedAccessException("project_preview_asset_forbidden");
            using var input = new ImmutableBlobStore(_instance, _contracts).OpenVerified(asset.Content);
            if (input.Length is < 1 or > 524288) throw Unavailable();
            var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
            using var parsed = StrictJson.Parse(bytes); Current(entry, requireCaller);
            return new(asset.Name, asset.Content.Sha256, Convert.ToBase64String(bytes));
        }
    }

    internal bool Release(string subject, Guid previewId)
    {
        RequireSubject(subject);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); Expire();
            if (!_entries.TryGetValue(previewId, out var entry)) return false;
            if (entry.Subject != subject) throw new UnauthorizedAccessException("project_preview_subject_unbound");
            Remove(entry); return true;
        }
    }

    private void Current(Entry entry, Action requireCaller)
    {
        requireCaller(); _lease.VerifyHeld(_instance);
        lock (_gate)
            if (_disposed || !entry.Root.IsActive || entry.OwnerLifetime.IsCancellationRequested || entry.Expires <= _clock.GetUtcNow() ||
                !_entries.TryGetValue(entry.Id, out var active) || !ReferenceEquals(entry, active)) throw Unavailable();
        var value = entry.Source; var definitions = new AutomationDefinitionStore(_database).Read();
        var binding = value.IsScript ? null : definitions.Bindings.SingleOrDefault(item => item.Id == value.OwnerId);
        var script = definitions.Scripts.SingleOrDefault(item => item.Id == (value.IsScript ? value.OwnerId : binding?.ScriptId));
        var root = definitions.Roots.SingleOrDefault(item => item.Id == script?.RootId);
        if ((value.IsScript ? script?.Revision : binding?.Revision) != value.OwnerRevision || script?.Revision != value.ScriptRevision ||
            root?.Id != value.RootId || root.Revision != value.RootRevision || PhysicalObjectIdentity.Read(root.Path) != value.RootIdentity)
            throw Unavailable();
        if (_installed.ReadCurrent(value.Consumer.ExtensionId)?.Metadata != value.Consumer ||
            _installed.ReadCurrent(value.Provider.ExtensionId)?.Metadata != value.Provider) throw Unavailable();
        _ = _activator.RequirePublishedRuntime(value.Consumer.ExtensionId); _ = _activator.RequirePublishedRuntime(value.Provider.ExtensionId);
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        new ConfigurationRevisionStore(_instance, _database, _contracts).RequireReadSet(connection, transaction, value.ReadSet);
    }

    private async Task SweepAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try { while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false)) lock (_gate) Expire(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }
    private void Expire()
    { foreach (var entry in _entries.Values.Where(item => item.Expires <= _clock.GetUtcNow() || item.OwnerLifetime.IsCancellationRequested).ToArray()) Remove(entry); }
    private void Remove(Entry entry) { _entries.Remove(entry.Id); entry.Root.Dispose(); }
    private static void RequireSubject(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 256 || subject.Any(char.IsControl)) throw new ArgumentException("project_subject_invalid");
    }
    private static IOException Unavailable() => new("project_preview_unavailable");
    public async ValueTask DisposeAsync()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        await _lifetime.CancelAsync().ConfigureAwait(false); await _sweep.ConfigureAwait(false);
        lock (_gate) foreach (var entry in _entries.Values.ToArray()) Remove(entry);
        _lifetime.Dispose();
    }
}
