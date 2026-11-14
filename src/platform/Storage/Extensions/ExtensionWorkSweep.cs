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
    private DatabaseVersion _scanRevision;
    private DatabaseVersion? _completedRevision;
    private string? _completedScopes, _scanningScopes;
    private bool _unresolved;
    private bool _inventoryUnavailable;
    private DateTimeOffset _retryAt;

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
                var observed = writers.Observe(candidate.JournalId);
                if (!observed.IsComplete || observed.State != WriterState.Stopped)
                { if (!candidate.CurrentWriter) _unresolved = true; continue; }
                if (!await cleanup.RunNextPageAsync(candidate.JournalId, cancellation).ConfigureAwait(false))
                    Block(candidate.OperationId, "page_unresolved");
            }
            catch (IOException error) when (error.Message is "extension_work_cleanup_resource_unavailable" or
                "extension_work_scope_referenced" or "extension_scope_reserved") { _unresolved = true; }
            catch (SqliteException error) when (error.SqliteErrorCode == 5)
            {
                _entries?.Dispose(); _entries = null; _completedRevision = null; _unresolved = true;
                _retryAt = DateTimeOffset.UtcNow.AddSeconds(1);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException or
                System.ComponentModel.Win32Exception or System.Text.Json.JsonException or InvalidOperationException)
            { Block(candidate.OperationId, error.GetType().Name); }
        }
    }

    private IReadOnlyList<WorkCandidate> ReadCandidates(int limit)
    {
        var published = activator.ReadPublishedScopes();
        var scopes = string.Join(',', published.Select(scope => scope.ActivationId).Order());
        var revision = DatabaseVersion.Read(database, null, DatabaseDomain.Runtime | DatabaseDomain.Extensions | DatabaseDomain.System);
        if (_entries is null && _completedRevision == revision && _completedScopes == scopes) return [];
        if (_entries is null && DateTimeOffset.UtcNow < _retryAt && _scanRevision == revision && _scanningScopes == scopes) return [];
        var root = PhysicalPathPolicy.NormalizeWithoutLinks(roots.ExtensionPayloadRoot);
        if (!Directory.Exists(root)) return [];
        if (_entries is null)
        {
            _scanRevision = revision;
            _scanningScopes = scopes; _unresolved = false;
            _entries = new DirectoryInfo(root).EnumerateDirectories(ExtensionWorkspacePath.Prefix + "*",
                new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }).GetEnumerator();
        }
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT j.journal_id,EXISTS(SELECT 1 FROM main.journal_writers w " +
            "WHERE w.journal_id=j.journal_id AND w.writer_id=$writer) FROM main.operations o JOIN main.operation_journals j " +
            "ON j.journal_id=o.initial_journal_id AND j.operation_id=o.operation_id " +
            "WHERE o.operation_id=$operation AND o.operation_kind='extension.apply' " +
            "AND j.operation_kind='extensionApply' AND j.phase='committed';";
        command.Parameters.AddWithValue("$writer", JournalWriterStore.CurrentWriter.WriterId.ToString("D"));
        var operation = command.Parameters.Add("$operation", SqliteType.Text);
        var candidates = new List<WorkCandidate>();
        for (var scanned = 0; scanned < limit * 16 && candidates.Count < limit; scanned++)
        {
            if (!_entries.MoveNext())
            {
                _entries.Dispose(); _entries = null;
                _completedRevision = _unresolved ? null : _scanRevision; _completedScopes = _scanningScopes;
                _retryAt = _unresolved ? DateTimeOffset.UtcNow.AddMinutes(1) : default;
                break;
            }
            var entry = _entries.Current;
            if (!entry.Name.StartsWith(ExtensionWorkspacePath.Prefix, StringComparison.Ordinal) ||
                !Guid.TryParseExact(entry.Name.AsSpan(ExtensionWorkspacePath.Prefix.Length), "N", out var id) ||
                _blocked.Contains(id)) continue;
            operation.Value = id.ToString("D");
            using var row = command.ExecuteReader();
            if (!row.Read()) continue;
            var journal = Guid.ParseExact(row.GetString(0), "D");
            var currentWriter = row.GetInt64(1) != 0;
            if (row.Read()) throw new IOException("extension_work_producer_ambiguous");
            candidates.Add(new(id, journal, currentWriter));
        }
        return candidates;
    }

    private void Block(Guid operationId, string reason)
    {
        _blocked.Add(operationId);
        Console.Error.WriteLine($"extension_work_cleanup_blocked {operationId:D} {reason}");
    }

    private sealed record WorkCandidate(Guid OperationId, Guid JournalId, bool CurrentWriter);
    public void Dispose() { _entries?.Dispose(); _entries = null; }
}
