using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed class WorkEvidenceScope : IDisposable
{
    private const int MaximumProtectedFiles = 4096;
    private const long MaximumProtectedBytes = 67108864;
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly ContractReader _contracts;
    private readonly PayloadApplyRoots _roots;
    private readonly List<IDisposable> _held = [];
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, (string Directory, string[] Names)> _journals = [];
    private IReadOnlyList<WorkHistoryEntry> _previous = [];
    private BoundExtensionPayload _producer = null!;
    private BoundExtensionInstallation? _installation;
    private readonly Dictionary<Guid, IReadOnlyList<FileEffect>> _effects = [];
    private long _bytes;
    private int _disposed;
    internal ImmutableHashSet<int> Previous { get; private set; } = ImmutableHashSet<int>.Empty;
    internal WorkTreeProtection? Tree { get; private set; }

    private WorkEvidenceScope(RuntimeInstance instance, StateDatabase database, ContractReader contracts, PayloadApplyRoots roots)
    { _instance = instance; _database = database; _contracts = contracts; _roots = roots; }

    internal static WorkEvidenceScope? TryAcquire(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        PayloadApplyRoots roots, FrozenFileCleanup plan, ExtensionInstalledMetadata? installed)
    {
        var result = new WorkEvidenceScope(instance, database, contracts, roots);
        try { result.Acquire(plan, installed); return result; }
        catch (ProtectionBudgetExceeded) { result.Dispose(); return null; }
        catch (IOException) { result.Dispose(); return null; }
        catch { result.Dispose(); throw; }
    }

    private void Acquire(FrozenFileCleanup plan, ExtensionInstalledMetadata? installed)
    {
        HoldInstanceIdentity();
        var work = plan.ExtensionWork ?? throw Invalid();
        Hold(work.Reference); Hold(work.Origin.Reference); HoldPayload(work.Origin.Producer);
        var current = new ExtensionInstalledEvidence(_instance, _database, _contracts, _roots)
            .ReadCurrent(work.Origin.Producer.Source.ExtensionId);
        if (current?.Metadata != installed) throw Invalid();
        if (current is not null) HoldPayload(current.Operation);
        var history = new ExtensionWorkHistory(_instance, _database, _contracts, _roots);
        foreach (var item in history.Capture(work))
        {
            Hold(item.Bound.Plan.ExtensionWork!.Reference);
            HoldJournal(item.Registered.JournalId);
        }
        // Bind again only after every input is held against replacement and writing.
        var actual = new ExtensionWorkCodec(_instance, _database, _contracts, _roots).ReadPage(work.Reference);
        if (actual.Origin.Reference != work.Origin.Reference) throw Invalid();
        _producer = actual.Origin.Producer;
        _installation = new ExtensionInstalledEvidence(_instance, _database, _contracts, _roots).ReadCurrent(_producer.Source.ExtensionId);
        if (_installation?.Metadata != installed) throw Invalid();
        if (_installation?.Metadata.IsInstalled == true)
        {
            var protectedIds = _installation.Footprint.Payload.OriginalObjects.Select(item => item.Identity).ToHashSet();
            if (actual.Origin.Proof.Nodes.Any(node => protectedIds.Contains(node.Identity))) throw Invalid();
        }
        FreezeEffects(_producer);
        if (_installation is not null) FreezeEffects(_installation.Operation);
        _previous = history.Capture(actual); Previous = history.RequirePage(actual);
        RequireFiles();
        var document = new JournalStore(_instance, _contracts).Read(plan.JournalId);
        var removed = ExtensionWorkInventory.Removed(plan, JsonNode.Parse(document.Export())!.AsObject(), Previous);
        Tree = WorkTreeProtection.Acquire(actual.Origin, removed);
    }

    private void HoldInstanceIdentity()
    {
        var reader = new InstanceRoot(_contracts);
        if (reader.OpenExisting(_instance.RuntimeRoot) != _instance) throw Invalid();
        var path = Path.Combine(_instance.RuntimeRoot, "instance.json");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > StrictJson.MaximumBytes) throw Invalid();
        HoldFile(path, bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        if (reader.OpenExisting(_instance.RuntimeRoot) != _instance) throw Invalid();
    }

    internal void RequireFiles()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        foreach (var item in _journals.Values)
            if (!Directory.EnumerateFileSystemEntries(item.Directory).Take(JournalCapacityLimits.MaximumRevisions + 1)
                .Select(Path.GetFileName).Order(StringComparer.Ordinal).SequenceEqual(item.Names)) throw Invalid();
    }

    internal void RequireInside(SqliteConnection connection, SqliteTransaction transaction)
    {
        RequireFiles();
        var binding = new ExtensionPayloadBinding(_instance, _database, _contracts, _roots);
        binding.RequireInside(connection, transaction, _producer, _effects[_producer.Plan.JournalId]);
        if (_installation is not null && _installation.Operation.Plan.JournalId != _producer.Plan.JournalId)
            binding.RequireInside(connection, transaction, _installation.Operation, _effects[_installation.Operation.Plan.JournalId]);
        if (ExtensionInstalledRows.ReadCurrentMetadata(connection, transaction, _producer.Source.ExtensionId) != _installation?.Metadata)
            throw Invalid();
        foreach (var item in _previous)
        {
            var current = RecoveryJournalRegistry.ReadJournalInside(connection, transaction, item.Registered.JournalId);
            if (!Same(current, item.Registered) ||
                !FileEffectRows.Read(connection, current.JournalId, transaction).SequenceEqual(item.Effects)) throw Invalid();
            RequireClaims(connection, transaction, item);
        }
    }

    private void FreezeEffects(BoundExtensionPayload bound) => _effects[bound.Plan.JournalId] =
        PayloadEffectCursor.ExpectedEffects(bound.Registered, bound.Plan, bound.History, bound.Physical, bound.Restoration);

    private static void RequireClaims(SqliteConnection connection, SqliteTransaction transaction, WorkHistoryEntry item)
    {
        var expected = FileCleanupItems.Claims(item.Bound.Plan);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT resource_key,mode FROM main.journal_resource_claims WHERE journal_id=$journal ORDER BY resource_key LIMIT 4097;";
        command.Parameters.AddWithValue("$journal", item.Registered.JournalId.ToString("D"));
        using var rows = command.ExecuteReader(); var index = 0;
        while (rows.Read())
            if (index >= expected.Count || rows.GetString(0) != expected[index].ResourceKey ||
                rows.GetInt32(1) != (int)expected[index++].Mode) throw Invalid();
        if (index != expected.Count) throw Invalid();
    }

    private void HoldPayload(BoundExtensionPayload bound)
    {
        Hold(bound.PlanReference); Hold(bound.Plan.ExtensionSource!);
        foreach (var entry in bound.Physical) Hold(entry.Intent.Artifact);
        if (bound.PreparationIntent is { } preparation) Hold(preparation.Artifact);
        if (bound.CommitIntent is { } commit) Hold(commit.Artifact);
        if (bound.Commit is { } installed) Hold(installed.Footprint);
        if (bound.Source.OriginalFootprint is { } previous) Hold(previous);
        foreach (var entry in bound.Restoration) Hold(entry.Intent.Artifact);
        HoldJournal(bound.Plan.JournalId);
    }

    private void HoldJournal(Guid id)
    {
        if (_journals.ContainsKey(id)) return;
        var documents = new JournalStore(_instance, _contracts).ReadAllRevisions(id);
        var directory = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(_instance.RuntimeRoot, "journals", id.ToString("D")));
        HoldDirectory(directory);
        foreach (var document in documents)
        {
            var bytes = document.Export();
            var path = Path.Combine(directory, document.Revision.ToString("D10", System.Globalization.CultureInfo.InvariantCulture) + ".json");
            HoldFile(path, bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        var last = documents[^1];
        var planPath = Path.Combine(_instance.RuntimeRoot, last.PlanPath);
        Hold(new(last.PlanPath, new FileInfo(planPath).Length, last.PlanDigest));
        _journals.Add(id, (directory, documents.Select(document => document.Revision.ToString("D10",
            System.Globalization.CultureInfo.InvariantCulture) + ".json").ToArray()));
    }

    private void Hold(BlobReference reference)
    {
        ExtensionWorkCodec.Reference(reference);
        HoldFile(PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(_instance.RuntimeRoot, reference.RelativePath)),
            reference.SizeBytes, reference.Sha256);
    }

    private void HoldFile(string path, long size, string digest)
    {
        if (_paths.Contains(path)) return;
        if (_paths.Count >= MaximumProtectedFiles || size > MaximumProtectedBytes - _bytes) throw new ProtectionBudgetExceeded();
        HoldDirectory(Path.GetDirectoryName(path)!);
        var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (input.Length != size || Convert.ToHexStringLower(SHA256.HashData(input)) != digest) throw Invalid();
            _held.Add(input); _paths.Add(path); _bytes += size;
        }
        catch { input.Dispose(); throw; }
    }

    private void HoldDirectory(string directory)
    {
        if (_directories.Add(directory)) _held.Add(StableDirectoryLease.Acquire(directory));
    }

    private static bool Same(RegisteredJournal left, RegisteredJournal right) =>
        left.JournalId == right.JournalId && left.OperationId == right.OperationId && left.Kind == right.Kind &&
        left.Phase == right.Phase && left.Revision == right.Revision && left.PlanPath == right.PlanPath &&
        left.PlanDigest == right.PlanDigest && left.ResourceKeys.SequenceEqual(right.ResourceKeys);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Tree?.Dispose(); Tree = null;
        for (var index = _held.Count - 1; index >= 0; index--) _held[index].Dispose();
        _held.Clear();
    }

    private sealed class ProtectionBudgetExceeded : Exception { }
    private static IOException Invalid() => new("extension_work_protected_evidence_changed");
}
