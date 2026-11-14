using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Automation;

internal sealed class AutomationAssetRetirement : IDisposable
{
    private readonly StateDatabase _database;
    private readonly Guid[] _scripts;
    private readonly string _ownershipDigest;
    private readonly List<(FrozenFileCleanup Plan, BlobReference Reference)> _plans = [];
    private readonly List<IDisposable> _held = [];
    private readonly JournalWriter _writer;

    private AutomationAssetRetirement(StateDatabase database, Guid[] scripts, string ownershipDigest)
        { _database = database; _scripts = scripts; _ownershipDigest = ownershipDigest; _writer = JournalWriterStore.CurrentWriter; }

    internal static AutomationAssetRetirement? Prepare(StateDatabase database, OperationIdentity operation, AutomationChanges changes)
    {
        if (changes.Removed.IsDefault) throw new AutomationDefinitionException("automation_changes_limit");
        var scripts = changes.Removed.Where(value => value.Kind == AutomationDefinitionKind.Script).Select(value => value.Id).Distinct().Order().ToArray();
        if (scripts.Length == 0) return null;
        IReadOnlyList<Ownership> ownership;
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Automation | DatabaseDomain.Content))
        using (var transaction = connection.BeginTransaction(deferred: true)) ownership = Read(connection, transaction, scripts);
        var result = new AutomationAssetRetirement(database, scripts, Digest(ownership));
        try
        {
            var retiring = scripts.ToHashSet();
            var candidates = ownership.GroupBy(value => value.AssetId).Where(group => group.All(value => retiring.Contains(value.ScriptId)))
                .Select(group => group.First().Blob).Distinct().OrderBy(value => value.Sha256, StringComparer.Ordinal).ToArray();
            if (candidates.Length == 0) return result;
            var instance = new InstanceRoot(new()).OpenExisting(database.RuntimeRoot);
            if (instance.InstanceId != database.InstanceId) throw new IOException("automation_retirement_instance_unbound");
            var blobs = new ImmutableBlobStore(instance, new());
            var codec = new CleanupPlanCodec(instance, new(), blobs);
            foreach (var batch in candidates.Chunk(256))
            {
                var plan = codec.Capture(operation.OperationId, Guid.NewGuid(), batch);
                var reference = codec.Persist(plan);
                result._held.Add(blobs.OpenVerified(reference));
                result._plans.Add((plan, reference));
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    internal void RetireOwners(SqliteConnection connection, SqliteTransaction transaction)
    {
        var ownership = Read(connection, transaction, _scripts);
        if (Digest(ownership) != _ownershipDigest) throw new AutomationConflictException();
        foreach (var item in _plans)
        {
            var journal = new RegisteredJournal(item.Plan.JournalId, item.Plan.OperationId, JournalKind.FileCleanup, JournalPhase.Prepared, 1,
                item.Reference.RelativePath, item.Reference.Sha256, item.Plan.Items.Select(value => "blob:" + value.Blob.Sha256).Order(StringComparer.Ordinal).ToArray());
            var effects = item.Plan.Items.Select(value => new FileEffect(journal.JournalId, value.StepId, FileEffectAction.RemoveOwned,
                FileEffectState.Pending, item.Reference.Sha256, null)).ToArray();
            RecoveryJournalRegistry.RegisterInside(connection, transaction, journal, effects, _writer);
        }
        foreach (var script in _scripts)
            AutomationRowWriter.Row(connection, transaction, "DELETE FROM automation.automation_asset_owners WHERE automation_id=$0;", script);
        foreach (var asset in ownership.Select(value => value.AssetId).Distinct())
            AutomationRowWriter.Row(connection, transaction, "DELETE FROM automation.automation_assets WHERE asset_id=$0 AND NOT EXISTS " +
                "(SELECT 1 FROM automation.automation_asset_owners WHERE asset_id=$0);", asset);
    }

    internal void MaterializeInitials()
    {
        if (_plans.Count == 0) return;
        var instance = new InstanceRoot(new()).OpenExisting(_database.RuntimeRoot);
        var registered = new RecoveryJournalRegistry(_database);
        foreach (var item in _plans)
        {
            var journal = registered.ReadJournal(item.Plan.JournalId);
            if (journal.Kind != JournalKind.FileCleanup || journal.Phase != JournalPhase.Prepared || journal.Revision != 1 ||
                journal.OperationId != item.Plan.OperationId || journal.PlanPath != item.Reference.RelativePath || journal.PlanDigest != item.Reference.Sha256)
                throw new IOException("automation_cleanup_registration_changed");
            new FileCleanupAcceptance(instance, new(), _database).MaterializeInitial(journal);
        }
    }

    private static IReadOnlyList<Ownership> Read(SqliteConnection connection, SqliteTransaction transaction, Guid[] scripts)
    {
        if (scripts.Length > 4096) throw new AutomationDefinitionException("automation_changes_limit");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var names = scripts.Select((script, ordinal) =>
        {
            var name = "$s" + ordinal;
            command.Parameters.AddWithValue(name, script.ToString("D"));
            return name;
        });
        command.CommandText = "SELECT a.asset_id,o.automation_id,a.digest,b.size_bytes,b.relative_path FROM automation.automation_assets a " +
            "JOIN automation.automation_asset_owners o USING(asset_id) JOIN content.blobs b USING(digest) WHERE EXISTS " +
            "(SELECT 1 FROM automation.automation_asset_owners s WHERE s.asset_id=a.asset_id AND s.automation_id IN (" + string.Join(',', names) +
            ")) ORDER BY a.asset_id,o.automation_id LIMIT 65537;";
        using var reader = command.ExecuteReader();
        var result = new List<Ownership>();
        while (reader.Read())
        {
            if (result.Count == 65536) throw new IOException("automation_retirement_inventory_limit");
            result.Add(new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), new(reader.GetString(4), reader.GetInt64(3), reader.GetString(2))));
        }
        return result.AsReadOnly();
    }

    private static string Digest(IReadOnlyList<Ownership> ownership) => Convert.ToHexStringLower(SHA256.HashData(BoundedJson.Serialize(ownership)));

    public void Dispose()
    {
        for (var index = _held.Count - 1; index >= 0; index--) _held[index].Dispose();
        _held.Clear();
    }

    private sealed record Ownership(Guid AssetId, Guid ScriptId, BlobReference Blob);
}
