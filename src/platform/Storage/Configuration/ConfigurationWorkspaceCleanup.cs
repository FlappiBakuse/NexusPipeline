using System.Runtime.Versioning;
using System.Text;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

[SupportedOSPlatform("windows")]
public sealed class ConfigurationWorkspaceCleanup(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
    InstanceLease lease, ResourceLeaseManager resources)
{
    private const int PageSize = 128;
    private readonly JournalStore _journals = new(instance, contracts);
    private readonly FileTreeScanner _scanner = new(SnapshotBudget.Default);
    private readonly HashSet<Guid> _pending = [];
    private bool _scanned;

    public async Task RunAsync(CancellationToken stopping)
    {
        try
        {
            await Task.Yield();
            while (!stopping.IsCancellationRequested)
            {
                try { await SweepAsync(stopping).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or Microsoft.Data.Sqlite.SqliteException)
                { Console.Error.WriteLine("configuration_cleanup_inventory_unavailable"); }
                await Task.Delay(TimeSpan.FromSeconds(2), stopping).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    internal async Task<bool> SweepAsync(CancellationToken stopping)
    {
        var inbox = database.ConfigurationCleanup;
        inbox.Start();
        if (!_scanned)
        {
            Guid? cursor = null;
            while (true)
            {
                stopping.ThrowIfCancellationRequested();
                var page = Eligible(cursor);
                foreach (var id in page) Clean(id, stopping);
                if (page.Count < PageSize) break;
                cursor = page[^1];
                await Task.Yield();
            }
            _scanned = true;
        }
        var changes = inbox.Take();
        try
        {
            foreach (var (table, rows) in changes)
                foreach (var batch in rows.Chunk(PageSize))
                {
                    stopping.ThrowIfCancellationRequested();
                    _pending.UnionWith(ReadChanged(table, batch));
                }
        }
        catch { inbox.Record(changes); throw; }
        foreach (var id in _pending.ToArray()) Clean(id, stopping);
        return _pending.Count == 0;
    }

    private void Clean(Guid id, CancellationToken stopping)
    {
        stopping.ThrowIfCancellationRequested();
        try
        {
            if (TryClean(id)) _pending.Remove(id);
            else _pending.Add(id);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                       System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
        {
            if (IsSharingConflict(error)) _pending.Add(id);
            else
            {
                _pending.Remove(id);
                Console.Error.WriteLine("configuration_workspace_cleanup_unverified " + id.ToString("D"));
            }
        }
    }

    private static bool IsSharingConflict(Exception error) => error switch
    {
        System.ComponentModel.Win32Exception { NativeErrorCode: 32 or 33 } => true,
        IOException io => io.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021) ||
                          io.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 32 or 33 },
        _ => false
    };

    internal bool TryClean(Guid journalId)
    {
        lease.VerifyHeld(instance);
        if (!Eligible(null, journalId).Contains(journalId)) return true;
        JournalDocument document;
        using (StartupProfile.Measure("configuration.cleanup.journal")) document = _journals.Read(journalId);
        if (document.Phase != JournalPhase.Committed) return true;
        var participants = document.Root.GetProperty("participants");
        if (participants.GetArrayLength() == 0) return true;
        var workspacePath = participants[0].GetProperty("identity").GetProperty("workPath").GetString()
            ?? throw new IOException("configuration_cleanup_workspace_unbound");
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(workspacePath);
        if (!Directory.Exists(path)) return true;
        var binding = ConfigurationJournalBinding.ReadForRestoration(instance, document, contracts, database);
        var prepared = binding.Prepared ?? throw new IOException("configuration_cleanup_workspace_unbound");
        if (path != prepared.WorkPath) throw new IOException("configuration_cleanup_workspace_unbound");
        using var root = resources.TryAcquire(Guid.NewGuid(), PhysicalResourceAdmission.Read(path)).Lease;
        if (root is null) return false;
        if (!Eligible(null, journalId).Contains(journalId) ||
            PhysicalObjectIdentity.Read(path) != prepared.WorkIdentity)
            throw new IOException("configuration_cleanup_workspace_unbound");
        StepWritersEvidence.RequireConfigurationStopped(instance, database, contracts, journalId);
        var stopped = new JournalWriterStore(database).ObserveForCurrentExecutor(journalId);
        if (!stopped.IsComplete || stopped.State != Core.Execution.WriterState.Stopped)
            throw new IOException("configuration_cleanup_stop_unverified");
        var planned = HasReceipt(instance, binding);
        var deletes = Inventory(prepared, planned);
        PublishReceipt(instance, binding);
        foreach (var deletion in deletes) PhysicalObjectDeletion.DeleteVerified(deletion);
        PhysicalObjectDeletion.DeleteVerified(new(path, prepared.Plan.ParentIdentity, prepared.WorkIdentity,
            FileObjectKind.Directory, 0, null));
        return true;
    }

    private IReadOnlyList<FileObjectDeletion> Inventory(PreparedConfigurationExchange prepared, bool planned)
    {
        var path = prepared.WorkPath;
        var members = Directory.GetFileSystemEntries(path);
        if (members.Length > 1 || members.Any(item => Path.GetFileName(item) is not ("candidate" or "spent")))
            throw new IOException("configuration_cleanup_workspace_unverified");
        if (members.Length == 0) return [];
        var candidate = members[0];
        var actual = _scanner.Capture(candidate, CancellationToken.None);
        var expected = prepared.Plan.Candidate.Snapshot;
        var known = expected.Entries.ToDictionary(item => item.RelativePath, StringComparer.Ordinal);
        var identities = prepared.CandidateObjects.ToDictionary(item => item.RelativePath, item => item.Identity, StringComparer.Ordinal);
        if (actual.Shape != expected.Shape || !planned && !actual.Entries.SequenceEqual(expected.Entries) ||
            actual.Entries.Any(entry => !known.TryGetValue(entry.RelativePath, out var exact) || entry != exact) ||
            PhysicalTreeIdentity.Capture(candidate, actual).Any(item => !identities.TryGetValue(item.RelativePath, out var exact) || item.Identity != exact))
            throw new IOException("configuration_cleanup_candidate_unverified");
        return actual.Entries.OrderByDescending(item => item.RelativePath.Length).Select(item =>
        {
            var relative = item.RelativePath;
            var slash = relative.LastIndexOf('/');
            var parentIdentity = relative.Length == 0 ? prepared.WorkIdentity : identities[slash < 0 ? "" : relative[..slash]];
            var target = relative.Length == 0 ? candidate : Path.Combine(candidate, relative.Replace('/', Path.DirectorySeparatorChar));
            return new FileObjectDeletion(target, parentIdentity, identities[relative], item.Kind, item.SizeBytes, item.Sha256);
        }).ToArray();
    }

    private IReadOnlyList<Guid> Eligible(Guid? after, Guid? exact = null)
    {
        var selection = exact.HasValue ? "j.journal_id=$id" : "j.journal_id>$id";
        return ReadEligible(selection, command => command.Parameters.AddWithValue("$id", (exact ?? after)?.ToString("D") ?? ""), paged: true);
    }

    private IReadOnlyList<Guid> ReadChanged(string table, long[] rows)
    {
        var parameters = string.Join(',', rows.Select((_, index) => "$row" + index));
        var filter = "(" + parameters + ")";
        var source = table switch
        {
            "operation_journals" => "SELECT p.journal_id FROM main.operation_journals p WHERE p.rowid IN " + filter +
                " UNION ALL SELECT r.journal_id FROM main.operation_journals p " +
                "JOIN configuration.configuration_run_sessions s ON s.parent_journal_id=p.journal_id " +
                "JOIN configuration.configuration_run_resources r USING(session_id) WHERE p.rowid IN " + filter,
            "configuration_run_sessions" => "SELECT r.journal_id FROM configuration.configuration_run_sessions s " +
                "JOIN configuration.configuration_run_resources r USING(session_id) WHERE s.rowid IN " + filter,
            "configuration_edit_sessions" => "SELECT r.journal_id FROM configuration.configuration_edit_sessions s " +
                "JOIN configuration.configuration_edit_resources r USING(session_id) WHERE s.rowid IN " + filter,
            "configuration_run_resources" => "SELECT journal_id FROM configuration.configuration_run_resources WHERE rowid IN " + filter,
            "configuration_edit_resources" => "SELECT journal_id FROM configuration.configuration_edit_resources WHERE rowid IN " + filter,
            _ => throw new IOException("configuration_cleanup_inventory_unavailable")
        };
        return ReadEligible("j.journal_id IN (" + source + ")", command =>
        {
            for (var index = 0; index < rows.Length; index++) command.Parameters.AddWithValue("$row" + index, rows[index]);
        }, paged: false);
    }

    private IReadOnlyList<Guid> ReadEligible(string selection, Action<Microsoft.Data.Sqlite.SqliteCommand> bind, bool paged)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true,
            domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT j.journal_id FROM main.operation_journals j WHERE j.phase='committed' AND " +
            selection + " AND ((j.operation_kind='configExchange' AND EXISTS(SELECT 1 FROM configuration.configuration_run_resources r " +
            "JOIN configuration.configuration_run_sessions s USING(session_id) JOIN main.operation_journals p ON p.journal_id=s.parent_journal_id " +
            "WHERE r.journal_id=j.journal_id AND s.phase IN (2,3) AND s.writer_gate=1 AND p.phase='committed')) " +
            "OR (j.operation_kind='configEdit' AND EXISTS(SELECT 1 FROM configuration.configuration_edit_resources r " +
            "JOIN configuration.configuration_edit_sessions s USING(session_id) WHERE r.journal_id=j.journal_id AND s.phase IN (2,3) AND s.writer_gate=1)))" +
            (paged ? " ORDER BY j.journal_id LIMIT 128;" : ";");
        bind(command);
        using var rows = command.ExecuteReader();
        var result = new List<Guid>();
        while (rows.Read()) result.Add(rows.GetGuid(0));
        return result;
    }

    internal static bool IsRetired(RuntimeInstance runtime, BoundConfigurationJournal binding)
    {
        if (binding.Physical.Phase != JournalPhase.Committed || binding.Prepared is not { } prepared) return false;
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(prepared.WorkPath);
        return !Directory.Exists(path) && !File.Exists(path) && HasReceipt(runtime, binding);
    }

    private static bool HasReceipt(RuntimeInstance runtime, BoundConfigurationJournal binding)
    {
        var path = ReceiptPath(runtime, binding);
        if (!File.Exists(path)) return false;
        var expected = Receipt(binding);
        using var input = File.OpenRead(path);
        if (input.Length != expected.Length) throw new IOException("configuration_cleanup_receipt_unbound");
        var bytes = new byte[expected.Length];
        input.ReadExactly(bytes);
        if (!bytes.AsSpan().SequenceEqual(expected)) throw new IOException("configuration_cleanup_receipt_unbound");
        return true;
    }

    private static void PublishReceipt(RuntimeInstance runtime, BoundConfigurationJournal binding)
    {
        if (HasReceipt(runtime, binding)) return;
        var path = ReceiptPath(runtime, binding);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var staging = path + "." + Guid.NewGuid().ToString("N") + ".pending";
        var bytes = Receipt(binding);
        using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(staging, path);
    }

    private static byte[] Receipt(BoundConfigurationJournal binding)
    {
        var prepared = binding.Prepared ?? throw new IOException("configuration_cleanup_workspace_unbound");
        return Encoding.UTF8.GetBytes(FormattableString.Invariant(
            $"nexus.configuration-workspace-removal/1\n{prepared.Plan.InstanceId:D}\n{binding.Physical.JournalId:D}\n{binding.Physical.Revision}\n{binding.Physical.PlanDigest}\n{prepared.WorkIdentity.VolumeSerial}\n{prepared.WorkIdentity.FileId}\n"));
    }

    private static string ReceiptPath(RuntimeInstance runtime, BoundConfigurationJournal binding) =>
        PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(runtime.RuntimeRoot, "staging", "configuration-cleanup",
            binding.Physical.JournalId.ToString("D") + ".receipt"));
}

internal sealed class ConfigurationCleanupInbox
{
    private readonly object _gate = new();
    private Dictionary<string, HashSet<long>>? _rows;

    internal static bool Tracks(string table) => table is "operation_journals" or "configuration_run_sessions" or
        "configuration_edit_sessions" or "configuration_run_resources" or "configuration_edit_resources";

    internal void Start()
    {
        lock (_gate) _rows ??= new(StringComparer.Ordinal);
    }

    internal void Record(IReadOnlyDictionary<string, HashSet<long>> changes)
    {
        if (Volatile.Read(ref _rows) is null || changes.Count == 0) return;
        lock (_gate)
            foreach (var (table, changed) in changes)
            {
                if (!Tracks(table)) continue;
                if (!_rows!.TryGetValue(table, out var ids)) _rows.Add(table, ids = []);
                ids.UnionWith(changed);
            }
    }

    internal Dictionary<string, HashSet<long>> Take()
    {
        lock (_gate)
        {
            var previous = _rows!;
            _rows = new(StringComparer.Ordinal);
            return previous;
        }
    }
}
