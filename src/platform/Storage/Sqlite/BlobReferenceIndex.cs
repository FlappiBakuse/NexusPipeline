using System.Text.Json;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Records;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Maintenance;

namespace NexusPipeline.Platform.Storage.Sqlite;

public sealed class BlobReferenceIndex
{
    private readonly RuntimeInstance _instance;
    private readonly ContractReader _contracts;
    private readonly StateDatabase _database;
    private readonly ImmutableBlobStore _blobs;
    private readonly PayloadApplyRoots? _payloadRoots;
    internal RuntimeInstance Instance => _instance;

    public BlobReferenceIndex(RuntimeInstance instance, ContractReader contracts, StateDatabase database, ImmutableBlobStore blobs, PayloadApplyRoots? payloadRoots = null)
    {
        if (database.InstanceId != instance.InstanceId || blobs.Instance.InstanceId != instance.InstanceId ||
            !string.Equals(database.DatabasePath, Path.Combine(instance.RuntimeRoot, "state", "nexus.db"), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(blobs.Instance.RuntimeRoot, instance.RuntimeRoot, StringComparison.OrdinalIgnoreCase))
            throw new IOException("blob_reference_index_instance_unbound");
        _instance = instance; _contracts = contracts; _database = database; _blobs = blobs;
        _payloadRoots = payloadRoots;
    }

    public bool IsReferenced(BlobReference blob)
        => ReadReferenced(new HashSet<BlobReference> { blob }).Contains(blob);

    internal Action? InventoryRead { get; init; }

    internal HashSet<BlobReference> ReadReferenced(IReadOnlySet<BlobReference> candidates)
    {
        if (candidates.Count is < 1 or > 256) throw new ArgumentException("blob_reference_batch_invalid");
        try
        {
            var referenced = new HashSet<BlobReference>();
            var content = new HashSet<BlobReference>();
            foreach (var blob in candidates)
            {
                if (CleanupCandidateFiles.SecretId(blob) is { } secretId)
                { if (SecretIsCurrent(blob, secretId)) referenced.Add(blob); continue; }
                if (blob.RelativePath != $"blobs/content/{blob.Sha256}.bin" || blob.SizeBytes < 0 || blob.Sha256.Length != 64 ||
                    blob.Sha256.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                    throw new ArgumentException("blob_reference_query_invalid");
                content.Add(blob);
                using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true);
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT EXISTS(SELECT 1 FROM automation_assets WHERE digest=$digest) " +
                    "OR EXISTS(SELECT 1 FROM configuration_revision_assets WHERE digest=$digest);";
                command.Parameters.AddWithValue("$digest", blob.Sha256);
                if ((long)command.ExecuteScalar()! != 0 || RetiredAssetReferences.IsReferenced(_instance, _database, _contracts, blob))
                    referenced.Add(blob);
            }
            InventoryRead?.Invoke();
            referenced.UnionWith(Notifications.DeliveryReferenceIndex.ReadReferenced(_instance, _database, _contracts, candidates));
            if (content.Count == 0) return referenced;
            referenced.UnionWith(MaintenancePendingInventory.ReadReferenced(_instance, _database, _contracts, _payloadRoots, content));
            var digests = content.GroupBy(item => item.Sha256).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            foreach (var digest in ExtensionInstalledRows.ReadArchiveDigests(_database))
                if (digests.TryGetValue(digest, out var installed)) referenced.UnionWith(installed);
            if (referenced.IsSupersetOf(content)) return referenced;
            if (OperatingSystem.IsWindows())
                new Secrets.SecretDocumentInventory(_instance, _database, _contracts).VisitReferences(item =>
                {
                    if (digests.TryGetValue(item.Sha256, out var matches)) referenced.UnionWith(matches);
                });
            VisitJournalReferences(item =>
            {
                if (digests.TryGetValue(item.Sha256, out var matches)) referenced.UnionWith(matches);
            });
            return referenced;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or SqliteException)
        {
            throw new IOException("blob_reference_evidence_unrecognized", error);
        }
    }

    private void VisitJournalReferences(Action<BlobReference> include)
    {
        foreach (var journal in new RecoveryJournalRegistry(_database).ReadHistory())
        {
            var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(_instance.RuntimeRoot, journal.PlanPath));
            var reference = new BlobReference(journal.PlanPath, new FileInfo(path).Length, journal.PlanDigest);
            if (journal.Kind is JournalKind.ConfigExchange or JournalKind.ConfigEdit)
            {
                var codecForPlan = new ConfigurationPlanCodec(_instance, _contracts, _blobs);
                var plan = codecForPlan.ReadStructure(reference);
                if (plan.JournalId != journal.JournalId || plan.OperationId != journal.OperationId || plan.Kind != journal.Kind)
                    throw new IOException("blob_reference_plan_unbound");
                foreach (var item in codecForPlan.References(plan)) include(item);
                var directory = Path.Combine(_instance.RuntimeRoot, "journals", journal.JournalId.ToString("D"));
                if (Directory.Exists(directory))
                {
                    var physical = new JournalStore(_instance, _contracts).Read(journal.JournalId);
                    using var document = StrictJson.Parse(physical.Export());
                    var participants = document.RootElement.GetProperty("participants");
                    if (participants.GetArrayLength() > 2) throw new IOException("blob_reference_edit_participant_unknown");
                    if (participants.GetArrayLength() == 2)
                    {
                        if (participants[1].GetProperty("kind").GetString() != "file" || participants[1].GetProperty("identity").GetProperty("format").GetString() != "nexus.configuration.edit-output-reference")
                            throw new IOException("blob_reference_edit_participant_unknown");
                        var workspace = new ConfigurationWorkspaceCodec(_contracts).Decode(participants[0].GetProperty("identity"), plan, journal.PlanDigest);
                        var codec = new EditOutputCodec(_instance, _contracts, _blobs);
                        var output = codec.Read(codec.ReadReference(participants[1].GetProperty("identity")), workspace, journal.PlanDigest, _database, verifyContents: false);
                        foreach (var item in output.Plan.Candidate.Contents) include(item);
                    }
                }
            }
            else if (journal.Kind == JournalKind.FileCleanup)
            {
                var plan = CleanupJournalBinding.ReadPlan(_instance, _contracts, journal, _database, _payloadRoots);
                if (plan.JournalId != journal.JournalId || plan.OperationId != journal.OperationId)
                    throw new IOException("blob_reference_plan_unbound");
            }
            else if (journal.Kind == JournalKind.RunAttempt)
            {
                foreach (var item in new PlanReferenceClosure(_instance, _contracts, _blobs, _database).Read(journal, reference)) include(item);
            }
            else if (journal.Kind is JournalKind.UpdateApply or JournalKind.ExtensionApply)
            {
                if (_payloadRoots is null) throw new IOException("blob_reference_payload_roots_unavailable");
                var plan = new PayloadPlanCodec(_instance, _contracts, _blobs, _payloadRoots).ReadStructure(reference, out var extensionSource);
                if (plan.JournalId != journal.JournalId || plan.OperationId != journal.OperationId || plan.Kind != journal.Kind || !plan.ResourceKeys.SequenceEqual(journal.ResourceKeys))
                    throw new IOException("blob_reference_plan_unbound");
                if (journal.Kind != JournalKind.ExtensionApply || journal.Phase != JournalPhase.Committed)
                    foreach (var item in plan.Before.Contents.Concat(plan.Candidate.Contents)) include(item);
                if (plan.MaintenanceSource is { } maintenanceReference)
                {
                    var sourceCodec = new UpdateSourceCodec(_instance, _contracts);
                    var source = sourceCodec.ReadMetadata(maintenanceReference); sourceCodec.RequireFrozenBinding(source, plan);
                    foreach (var item in UpdateSourceCodec.References(source)) include(item);
                }
                if (extensionSource is { } extension)
                {
                    var unfinished = journal.Phase != JournalPhase.Committed;
                    foreach (var item in PayloadSourceCodec.References(extension, includeArchive: unfinished)) include(item);
                    if (unfinished && new PayloadSourceCodec(_instance, _contracts).ReadOriginalArchive(extension, plan, _payloadRoots) is { } original)
                        include(original);
                }
            }
            else if (journal.Kind == JournalKind.AssetPrepare)
            {
                var plan = new AssetPlanCodec(_instance, _contracts, _blobs).Read(reference).ReadWire();
                if (plan.JournalId != journal.JournalId.ToString("D") || plan.OperationId != journal.OperationId.ToString("D") ||
                    !plan.ResourceKeys.SequenceEqual(journal.ResourceKeys)) throw new IOException("blob_reference_plan_unbound");
                if (RetainsAssetContents(journal))
                    foreach (var asset in plan.Assets) include(new(asset.Content.RelativePath, asset.Content.SizeBytes, asset.Content.Sha256));
            }
            else if (journal.Kind == JournalKind.SecretPrepare)
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                var plan = new Secrets.PreparationPlanCodec(_instance, _contracts, _database).Read(reference).ReadWire();
                if (plan.OperationId != journal.OperationId.ToString("D") || plan.JournalId != journal.JournalId.ToString("D") || !plan.ResourceKeys.SequenceEqual(journal.ResourceKeys))
                    throw new IOException("blob_reference_secret_plan_unbound");
                if (plan.NotificationPublication is { } notification)
                {
                    using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true);
                    using var command = connection.CreateCommand();
                    command.CommandText = "SELECT status FROM operations WHERE operation_id=$id AND request_digest=$digest AND operation_kind='notification.task.save';";
                    command.Parameters.AddWithValue("$id", notification.ParentOperationId);
                    command.Parameters.AddWithValue("$digest", notification.ParentRequestDigest);
                    var state = command.ExecuteScalar() as string;
                    if (state is not ("accepted" or "completed")) throw new IOException("blob_reference_notification_parent_unknown");
                    if (state == "accepted") include(new(notification.Ciphertext.RelativePath, notification.Ciphertext.SizeBytes, notification.Ciphertext.Sha256));
                }
                if (plan.DocumentPublication is { } document)
                {
                    using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true);
                    using var command = SecretDocumentRows.Command(connection, null, "SELECT status FROM operations " +
                        "WHERE operation_id=$0 AND request_digest=$1 AND operation_kind='extension.document.write';",
                        document.ParentOperationId, document.ParentRequestDigest);
                    var state = command.ExecuteScalar() as string;
                    if (state is not ("accepted" or "completed")) throw new IOException("blob_reference_secret_document_unknown");
                    if (state == "accepted") include(new(document.Ciphertext.RelativePath, document.Ciphertext.SizeBytes, document.Ciphertext.Sha256));
                }
            }
            else throw new IOException("blob_reference_kind_not_implemented");
        }
    }

    private bool SecretIsCurrent(BlobReference item, Guid id)
    {
        CleanupCandidateFiles.Validate(item);
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT digest,size_bytes,state,EXISTS(SELECT 1 FROM secret_fields f WHERE f.secret_id=p.secret_id) " +
            "FROM secret_payloads p WHERE secret_id=$id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        using var row = command.ExecuteReader();
        if (!row.Read() || row.GetString(0) != item.Sha256 || row.GetInt64(1) != item.SizeBytes ||
            row.GetString(2) is not ("prepared" or "committed" or "retired")) throw new IOException("cleanup_secret_reference_unverified");
        var current = row.GetBoolean(3) || row.GetString(2) != "retired";
        row.Dispose();
        return current;
    }

    private bool RetainsAssetContents(RegisteredJournal journal)
    {
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT state FROM automation_imports WHERE operation_id=$operation AND journal_id=$journal;";
        command.Parameters.AddWithValue("$operation", journal.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$journal", journal.JournalId.ToString("D"));
        var value = command.ExecuteScalar();
        if (value is not long state || state is < 0 or > 3) throw new IOException("blob_reference_asset_owner_unrecognized");
        return journal.Phase != JournalPhase.Committed || state is not (1 or 2);
    }
}

