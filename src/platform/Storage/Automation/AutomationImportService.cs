using System.Security.Cryptography;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Automation;

public sealed class AutomationImportService(RuntimeInstance instance, ContractReader contracts, StateDatabase database,
    InstanceLease instanceLease, ResourceLeaseManager resources, RecoveryIndex index, NexusPipeline.Platform.Storage.Journals.PayloadApplyRoots? payloadRoots = null)
{
    public AutomationImportPreview Preview(Guid operationId, ReadOnlyMemory<byte> input, IReadOnlyDictionary<Guid, string> selectedRoots, long expectedRevision) =>
        new AutomationImportPreviewer(instance, contracts, database).Preview(operationId, input, selectedRoots, expectedRevision);

    public AutomationImportResult Apply(Guid operationId, string subjectId, ReadOnlyMemory<byte> input,
        IReadOnlyDictionary<Guid, string> selectedRoots, long expectedRevision, string previewDigest,
        IReadOnlyDictionary<Guid, string> selectedAttachmentFiles, CancellationToken cancellationToken = default)
    {
        instanceLease.VerifyHeld(instance);
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot || input.Length is 0 or > StrictJson.MaximumBytes)
            throw new AutomationDefinitionException("automation_import_request_invalid");
        var ownedInput = input.ToArray();
        var roots = selectedRoots.ToDictionary(value => value.Key, value => value.Value);
        var digest = Convert.ToHexStringLower(SHA256.HashData(ownedInput));
        var operation = AssetPreparationAcceptance.Identity(operationId, subjectId, digest, previewDigest, expectedRevision, roots);
        var replay = Lookup(operation);
        if (replay is not null) return replay;
        var parsed = new AutomationBundleCodec(contracts).Decode(ownedInput, roots, expectedRevision);
        var claims = parsed.Attachments.Select(value => "blob:" + value.Sha256).Append("automation:definitions")
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(key => new ResourceClaim(key, ResourceClaimMode.Mutation)).Concat(parsed.Definitions.Scripts
                .Where(script => script.ExtensionId is not null).Select(script => new ResourceClaim("extension:" + script.ExtensionId, ResourceClaimMode.Reference)))
            .Distinct().ToArray();
        var admission = instanceLease.PublishExecution(() => resources.TryAcquire(operationId, claims));
        using var lease = admission.Lease ?? throw new AutomationDefinitionException(admission.Denial == ResourceLeaseDenial.Conflict
            ? "automation_resource_conflict" : "automation_host_unavailable");
        replay = Lookup(operation);
        if (replay is not null) return replay;
        var preview = Preview(operationId, ownedInput, roots, expectedRevision);
        AutomationImportPreviewer.RequireSamePreview(preview, digest, previewDigest);
        cancellationToken.ThrowIfCancellationRequested();
        SpecializedScriptPolicy.Require(instance, database, contracts, payloadRoots, parsed.Definitions.Scripts);
        if (!parsed.Attachments.IsEmpty)
        {
            using var prepared = new AssetPlanCodec(instance, contracts, new(instance, contracts)).Freeze(preview, ownedInput, Guid.NewGuid());
            var selected = selectedAttachmentFiles.ToDictionary(value => AutomationTemplateIdentity.AttachmentId(operationId, value.Key, parsed.IsScriptTemplate), value => value.Value);
            using var sources = new AssetPreparationSources(prepared.Plan, selected);
            return new AssetPreparationExecutor(instance, contracts, database, index).Execute(operation, prepared, sources, instanceLease, lease, cancellationToken);
        }
        if (selectedAttachmentFiles.Count != 0) throw new AutomationDefinitionException("automation_attachment_selection_unbound");
        return CommitDefinitions(operation, preview);
    }

    public AutomationImportResult? ReadAccepted(Guid operationId, string subjectId, ReadOnlyMemory<byte> input,
        IReadOnlyDictionary<Guid, string> selectedRoots, long expectedRevision, string previewDigest)
    {
        instanceLease.VerifyHeld(instance);
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot ||
            input.Length is 0 or > StrictJson.MaximumBytes) throw new AutomationDefinitionException("automation_import_request_invalid");
        var digest = Convert.ToHexStringLower(SHA256.HashData(input.ToArray()));
        return Lookup(AssetPreparationAcceptance.Identity(operationId, subjectId, digest, previewDigest,
            expectedRevision, selectedRoots.ToDictionary(item => item.Key, item => item.Value)));
    }

    public PreparedAutomationExport PrepareExport(DateTimeOffset createdAt, Guid? templateScriptId = null) =>
        new AutomationExportService(instance, contracts, database).Prepare(createdAt, templateScriptId);

    private AutomationImportResult CommitDefinitions(OperationIdentity operation, AutomationImportPreview preview)
    {
        var held = new List<IDisposable>();
        try
        {
            foreach (var root in preview.Roots) held.Add(new PhysicalDirectoryLease(root.Path, root.Identity));
            var result = new AutomationDefinitionStore(database).CommitImport(operation, preview.Bundle.Definitions, DateTimeOffset.UtcNow);
            return Published(result);
        }
        finally { for (var ordinal = held.Count - 1; ordinal >= 0; ordinal--) held[ordinal].Dispose(); }
    }

    private AutomationImportResult? Lookup(OperationIdentity operation)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Automation);
        using var transaction = connection.BeginTransaction(deferred: true);
        if (OperationRows.ReadExisting(connection, transaction, operation) is null) return null;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT initial_journal_id FROM main.operations WHERE operation_id=$id;";
        command.Parameters.AddWithValue("$id", operation.OperationId.ToString("D"));
        return command.ExecuteScalar() is DBNull ? Published(AutomationDefinitionStore.ReadResult(connection, transaction, operation.OperationId))
            : AssetPreparationAcceptance.ReadAccepted(connection, transaction, operation.OperationId, false).Result;
    }

    private static AutomationImportResult Published(AutomationChangeResult result) =>
        new(result.OperationId, null, AutomationImportState.Published, result.CatalogRevision, "automation_import_published", result.IsNew);
}
