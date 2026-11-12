using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class ExtensionWorkSweep(StateDatabase database, PayloadApplyRoots roots, RecoveryIndex recovery,
    ExtensionActivator activator, WorkCleanupService cleanup) : IDisposable
{
    private readonly HashSet<Guid> _blocked = [];
    private IEnumerator<DirectoryInfo>? _entries;
    private long _scanRevision;
    private long? _completedRevision;
    private string? _completedScopes, _scanningScopes;
    private bool _unresolved;
    private bool _inventoryUnavailable;

    internal async Task SweepAsync(int pageLimit, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!recovery.Current.IsReady) return;
        IReadOnlyList<WorkCandidate> pending;
        try { pending = ReadCandidates(pageLimit); _inventoryUnavailable = false; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException or FormatException)
        {
            _entries?.Dispose(); _entries = null;
            if (!_inventoryUnavailable) Console.Error.WriteLine("extension_work_inventory_unavailable");
            _inventoryUnavailable = true;
            return;
        }
        var writers = new JournalWriterStore(database);
        foreach (var candidate in pending)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!recovery.Current.IsReady) break;
            try
            {
                // A retired managed scope can leave its assembly mapped until the producer process exits.
                var observed = writers.Observe(candidate.JournalId);
                if (!observed.IsComplete || observed.State != WriterState.Stopped) { _unresolved = true; continue; }
                if (!await cleanup.RunNextPageAsync(candidate.JournalId, cancellation).ConfigureAwait(false))
                    Block(candidate.OperationId, "page_unresolved");
            }
            catch (IOException error) when (error.Message is "extension_work_cleanup_resource_unavailable" or
                "extension_work_scope_referenced" or "extension_scope_reserved") { _unresolved = true; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException or
                System.ComponentModel.Win32Exception or System.Text.Json.JsonException or InvalidOperationException)
            { Block(candidate.OperationId, error.GetType().Name); }
        }
    }

    private IReadOnlyList<WorkCandidate> ReadCandidates(int limit)
    {
        var published = activator.ReadPublishedScopes();
        var scopes = string.Join(',', published.Select(scope => scope.ActivationId).Order());
        if (_entries is null && _completedRevision == database.CommitSequence && _completedScopes == scopes) return [];
        var root = PhysicalPathPolicy.NormalizeWithoutLinks(roots.ExtensionPayloadRoot);
        if (!Directory.Exists(root)) return [];
        var active = published.Select(scope => scope.ExtensionId).ToHashSet(StringComparer.Ordinal);
        if (_entries is null)
        {
            _scanRevision = database.CommitSequence;
            _scanningScopes = scopes; _unresolved = false;
            _entries = new DirectoryInfo(root).EnumerateDirectories(ExtensionWorkspacePath.Prefix + "*",
                new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }).GetEnumerator();
        }
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT j.journal_id,r.extension_id FROM operations o JOIN operation_journals j " +
            "ON j.journal_id=o.initial_journal_id AND j.operation_id=o.operation_id LEFT JOIN extension_installation_revisions r " +
            "ON r.journal_id=j.journal_id WHERE o.operation_id=$operation AND o.operation_kind='extension.apply' " +
            "AND j.operation_kind='extensionApply' AND j.phase='committed';";
        var operation = command.Parameters.Add("$operation", SqliteType.Text);
        var candidates = new List<WorkCandidate>();
        for (var scanned = 0; scanned < limit * 16 && candidates.Count < limit; scanned++)
        {
            if (!_entries.MoveNext())
            {
                _entries.Dispose(); _entries = null;
                _completedRevision = _unresolved ? null : _scanRevision; _completedScopes = _scanningScopes;
                break;
            }
            var entry = _entries.Current;
            if (!entry.Name.StartsWith(ExtensionWorkspacePath.Prefix, StringComparison.Ordinal) ||
                !Guid.TryParseExact(entry.Name.AsSpan(ExtensionWorkspacePath.Prefix.Length), "N", out var id) ||
                _blocked.Contains(id)) continue;
            operation.Value = id.ToString("D");
            using var row = command.ExecuteReader();
            if (!row.Read() || !row.IsDBNull(1) && active.Contains(row.GetString(1))) continue;
            var journal = Guid.ParseExact(row.GetString(0), "D");
            if (row.Read()) throw new IOException("extension_work_producer_ambiguous");
            candidates.Add(new(id, journal));
        }
        return candidates;
    }

    private void Block(Guid operationId, string reason)
    {
        _blocked.Add(operationId);
        Console.Error.WriteLine($"extension_work_cleanup_blocked {operationId:D} {reason}");
    }

    private sealed record WorkCandidate(Guid OperationId, Guid JournalId);
    public void Dispose() { _entries?.Dispose(); _entries = null; }
}
