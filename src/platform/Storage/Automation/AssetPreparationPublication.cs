using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Automation;

internal sealed class AssetPreparationPublication(RuntimeInstance instance, ContractReader contracts, StateDatabase database)
{
    internal void VerifyDefinitions(FrozenAssetPreparation frozen)
    {
        var definitions = new AssetPreparationAcceptance(instance, contracts, database).ReadDefinitions(frozen.ReadWire()).Definitions;
        var before = new AutomationDefinitionStore(database).Read();
        _ = BoundedJson.Serialize(AutomationEdits.Apply(before, definitions));
        using var notificationSelections = Notifications.PreparedNotificationSelections.Capture(database);
        notificationSelections.RequireChanges(before, definitions);
    }

    internal AutomationImportResult Close(FrozenAssetPreparation frozen, BlobReference reference, JournalDocument committed, bool cancelled)
    {
        var plan = frozen.ReadWire();
        if (committed.Phase != JournalPhase.Committed || committed.JournalId.ToString("D") != plan.JournalId ||
            committed.PlanDigest != reference.Sha256 || AssetPreparationJournal.HasNoEffects(committed) != cancelled)
            throw new IOException("asset_publication_commit_unbound");
        var bundle = new AssetPreparationAcceptance(instance, contracts, database).ReadDefinitions(plan);
        using var notificationSelections = Notifications.PreparedNotificationSelections.Capture(database);
        return database.Write((connection, transaction) =>
        {
            var accepted = AssetPreparationAcceptance.ReadAccepted(connection, transaction, Guid.Parse(plan.OperationId), false).Result;
            if (accepted.State is AutomationImportState.Published or AutomationImportState.Cancelled)
            {
                if ((accepted.State == AutomationImportState.Cancelled) != cancelled) throw new IOException("asset_publication_result_conflict");
                return accepted;
            }
            if (accepted.State != AutomationImportState.Accepted) throw new IOException("asset_publication_isolated");
            var before = AutomationRows.Read(connection, transaction);
            var next = cancelled ? before : AutomationEdits.Apply(before, bundle.Definitions);
            if (!cancelled)
            {
                _ = BoundedJson.Serialize(next);
                notificationSelections.RequireChangesInside(connection, transaction, before, bundle.Definitions);
                AutomationRowWriter.Write(connection, transaction, before, next);
                Attach(connection, transaction, plan, bundle.Definitions);
                _ = AutomationRows.Read(connection, transaction);
            }
            var state = cancelled ? AutomationImportState.Cancelled : AutomationImportState.Published;
            var reason = cancelled ? "asset_preparation_not_executed" : "automation_import_published";
            AutomationRowWriter.Row(connection, transaction,
                "UPDATE automation.automation_imports SET state=$0,catalog_revision=$1,reason_code=$2 WHERE operation_id=$3 AND state=0;",
                (int)state, next.Revision, reason, accepted.OperationId);
            AutomationRowWriter.Row(connection, transaction, "UPDATE main.operations SET status='completed' WHERE operation_id=$0;", accepted.OperationId);
            var registered = RecoveryJournalRegistry.ReadInside(connection, transaction, accepted.JournalId ?? throw new IOException("asset_publication_journal_missing"));
            new RecoveryJournalRegistry(database).AdvanceInside(connection, transaction,
                registered with { Phase = JournalPhase.Committed, Revision = committed.Revision }, registered.Revision,
                cancelled ? new(true, false, false, false) : null);
            return new(accepted.OperationId, accepted.JournalId, state, next.Revision, reason, false);
        });
    }

    private static void Attach(SqliteConnection connection, SqliteTransaction transaction, AssetPreparationPlan plan, AutomationChanges definitions)
    {
        if (definitions.Scripts.IsEmpty) throw new IOException("asset_publication_requires_script_owner");
        foreach (var asset in plan.Assets)
        {
            AutomationRowWriter.Row(connection, transaction, "INSERT INTO content.blobs VALUES($0,$1,$2) ON CONFLICT(digest) DO NOTHING;",
                asset.Content.Sha256, asset.Content.SizeBytes, asset.Content.RelativePath);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT size_bytes,relative_path FROM content.blobs WHERE digest=$digest;";
            command.Parameters.AddWithValue("$digest", asset.Content.Sha256);
            using (var reader = command.ExecuteReader())
                if (!reader.Read() || reader.GetInt64(0) != asset.Content.SizeBytes || reader.GetString(1) != asset.Content.RelativePath)
                    throw new IOException("asset_publication_blob_conflict");
            AutomationRowWriter.Row(connection, transaction, "INSERT INTO automation.automation_assets VALUES($0,$1,$2,$3);",
                Guid.Parse(asset.AssetId), Guid.Parse(plan.OperationId), asset.PackagePath, asset.Content.Sha256);
            foreach (var script in definitions.Scripts)
                AutomationRowWriter.Row(connection, transaction, "INSERT INTO automation.automation_asset_owners VALUES($0,$1);", Guid.Parse(asset.AssetId), script.Id);
        }
    }
}
