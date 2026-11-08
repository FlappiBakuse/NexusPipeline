using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed class InstallationReadLease : IDisposable
{
    private readonly RuntimeInstance _instance;
    private readonly InstanceLease _lease;
    private readonly ExtensionPayloadBinding _binding;
    private readonly JournalHistoryProtection _history;
    private readonly DirectoryAttributeProtection _paths;
    private readonly List<IDisposable> _held = [];
    private BoundExtensionPayload _bound = null!;
    private IReadOnlyList<FileEffect> _effects = [];
    private ExtensionInstalledMetadata _expected = null!;
    private DatabaseValidationLease? _sql;
    private bool _disposed;

    private InstallationReadLease(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
        ContractReader contracts, PayloadApplyRoots roots, JournalHistoryProtection history, DirectoryAttributeProtection paths)
    { _instance = instance; _lease = lease; _binding = new(instance, database, contracts, roots); _history = history; _paths = paths; }

    [SupportedOSPlatform("windows")]
    internal static InstallationReadLease? TryAcquire(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
        ContractReader contracts, PayloadApplyRoots roots, Guid journal, Func<BoundExtensionPayload> read)
    {
        using var identity = InstanceReadScope.Hold(instance, contracts);
        using var reads = DatabaseReadScope.Hold(database.DatabasePath);
        using var blobsScope = BlobReadScope.Hold(instance);
        DirectoryAttributeProtection? paths = DirectoryAttributeProtection.Acquire(instance);
        try
        {
            using var scope = DirectoryAttributeScope.Enter(paths);
            var directory = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, "journals", journal.ToString("D")));
            paths.Include([directory]);
            var history = JournalHistoryProtection.TryAcquire(directory);
            if (history is null) return null;
            var result = new InstallationReadLease(instance, database, lease, contracts, roots, history, paths);
            paths = null;
            try
            {
                var bound = read();
                if (bound.Registered.Phase != JournalPhase.Committed || bound.Commit is null || bound.Footprint is not { IsInstalled: true } ||
                    !bound.Restoration.IsEmpty || bound.Registered.JournalId != journal) throw Invalid();
                result._paths.Include(new[] { bound.Footprint.TargetPath }.Concat(bound.Footprint.Payload.Snapshot.Entries
                    .Where(item => item.Kind == FileObjectKind.Directory)
                    .Select(item => Path.Combine(bound.Footprint.TargetPath, item.RelativePath))));
                history.Bind(instance, bound.History);
                var references = new List<BlobReference> { bound.PlanReference, bound.Plan.ExtensionSource!, bound.Commit.Footprint };
                references.AddRange(bound.Physical.Select(item => item.Intent.Artifact));
                if (bound.PreparationIntent is { } preparation) references.Add(preparation.Artifact);
                if (bound.CommitIntent is { } commit) references.Add(commit.Artifact);
                if (bound.Source.OriginalFootprint is { } previous) references.Add(previous);
                var blobs = new ImmutableBlobStore(instance, contracts);
                result._held.Add(StableDirectoryLease.Acquire(Path.Combine(instance.RuntimeRoot, "blobs", "plans")));
                foreach (var reference in references.Distinct()) result._held.Add(blobs.OpenVerified(reference));
                // The parsed installation remains valid only while its exact journal and metadata bytes cannot change.
                result._bound = bound;
                result._effects = PayloadEffectCursor.ExpectedEffects(bound.Registered, bound.Plan, bound.History, bound.Physical, bound.Restoration);
                result._expected = ExtensionPayloadBinding.DerivedInstallation(bound.Plan, bound.Commit, bound.Footprint);
                result._sql = new(database.DatabasePath);
                result.RequireCurrent();
                return result;
            }
            catch { result.Dispose(); throw; }
        }
        finally { paths?.Dispose(); }
    }

    internal IDisposable ProtectPaths()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return DirectoryAttributeScope.Enter(_paths);
    }

    internal BoundExtensionPayload ReadCurrent()
    {
        RequireCurrent();
        return _bound;
    }

    internal void RequireCurrent()
    {
        using var timing = StartupProfile.Measure("installation.revalidate");
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var paths = DirectoryAttributeScope.Enter(_paths);
        _lease.VerifyHeld(_instance);
        _ = _history.Read();
        _sql!.RequireCurrent((connection, transaction) =>
        {
            _binding.RequireInside(connection, transaction, _bound, _effects);
            if (ExtensionInstalledRows.ReadCurrentMetadata(connection, transaction, _expected.ExtensionId) != _expected ||
                HostSettingsRows.ReadCurrent(connection, transaction).Values.Extensions
                    .SingleOrDefault(item => item.ExtensionId == _expected.ExtensionId)?.Enabled != true) throw Invalid();
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { for (var index = _held.Count - 1; index >= 0; index--) _held[index].Dispose(); }
        finally
        {
            _held.Clear();
            try { try { _sql?.Dispose(); } finally { _history.Dispose(); } }
            finally { _paths.Dispose(); }
        }
    }

    private static IOException Invalid() => new("extension_installation_consumption_unavailable");
}
