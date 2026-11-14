using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Automation;

public sealed record AssetAcceptanceResult(AutomationImportResult Result, string PlanPath, string PlanDigest);

public sealed class AssetPreparationAcceptance(RuntimeInstance instance, ContractReader contracts, StateDatabase database)
{
    public static OperationIdentity Identity(AssetPreparationPlan plan, string subjectId) => Identity(Guid.Parse(plan.OperationId), subjectId,
        plan.Input.Sha256, plan.PreviewDigest, plan.ExpectedCatalogRevision, plan.Roots.ToDictionary(value => Guid.Parse(value.SourceRootId), value => value.Path));

    internal static OperationIdentity Identity(Guid operationId, string subjectId, string inputDigest, string previewDigest,
        long expectedRevision, IReadOnlyDictionary<Guid, string> selectedRoots)
    {
        if (!FileEffectRows.IsDigest(inputDigest) || !FileEffectRows.IsDigest(previewDigest) || expectedRevision is < 0 or > 9007199254740991 ||
            selectedRoots.Count > 128 || selectedRoots.Any(value => value.Key == Guid.Empty || PhysicalPathPolicy.NormalizeSyntax(value.Value) != value.Value))
            throw new AutomationDefinitionException("automation_import_request_invalid");
        var bytes = BoundedJson.Serialize(new
        {
            inputDigest, PreviewDigest = previewDigest, ExpectedCatalogRevision = expectedRevision,
            selectedRoots = selectedRoots.OrderBy(value => value.Key.ToString("D"), StringComparer.Ordinal)
                .Select(value => new { SourceRootId = value.Key.ToString("D"), Path = value.Value }).ToArray()
        });
        return new(operationId, subjectId, Convert.ToHexStringLower(SHA256.HashData(bytes)), "automation.import");
    }

    public AssetAcceptanceResult Accept(OperationIdentity operation, PreparedAssetPreparation prepared, DateTimeOffset acceptedAt)
    {
        var plan = prepared.Plan.ReadWire();
        var reference = prepared.Reference;
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot ||
            plan.InstanceId != instance.InstanceId.ToString("D") || operation.OperationId.ToString("D") != plan.OperationId ||
            operation != Identity(plan, operation.SubjectId)) throw new IOException("asset_acceptance_identity_unbound");
        var kind = Enum.Parse<JournalKind>(plan.OperationKind, true);
        var journal = new RegisteredJournal(Guid.Parse(plan.JournalId), operation.OperationId, kind, JournalPhase.Prepared, 1,
            reference.RelativePath, reference.Sha256, Array.AsReadOnly(plan.ResourceKeys.ToArray()));
        var writer = JournalWriterStore.CurrentWriter;
        var bundle = ReadDefinitions(plan);
        if (bundle.Definitions.Scripts.IsEmpty) throw new IOException("asset_preparation_requires_script_owner");
        var effects = plan.Assets.Where(value => !value.WasPresent).DistinctBy(value => value.Content.Sha256)
            .Select(value => new FileEffect(journal.JournalId, Step(value.Content.Sha256), FileEffectAction.InstallCandidate,
                FileEffectState.Pending, reference.Sha256, null)).ToArray();
        return database.Write((connection, transaction) =>
        {
            var acceptance = OperationRows.Accept(connection, transaction, operation, acceptedAt);
            if (!acceptance.IsNew) return ReadAccepted(connection, transaction, operation.OperationId, false);
            _ = AutomationEdits.Apply(AutomationRows.Read(connection, transaction), bundle.Definitions);
            VerifyAssetIds(connection, transaction, plan);
            RecoveryJournalRegistry.RegisterInside(connection, transaction, journal, effects, writer);
            AutomationRowWriter.Row(connection, transaction, "INSERT INTO automation.automation_imports VALUES($0,$1,0,NULL,'asset_preparation_pending');",
                operation.OperationId, journal.JournalId);
            return new(new(operation.OperationId, journal.JournalId, AutomationImportState.Accepted, null, "asset_preparation_pending", true), journal.PlanPath, journal.PlanDigest);
        });
    }

    private static void VerifyAssetIds(SqliteConnection connection, SqliteTransaction transaction, AssetPreparationPlan plan)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM automation.automation_assets WHERE asset_id=$id);";
        var parameter = command.Parameters.Add("$id", SqliteType.Text);
        foreach (var asset in plan.Assets)
        {
            parameter.Value = asset.AssetId;
            if ((long)command.ExecuteScalar()! != 0) throw new IOException("asset_identity_already_published");
        }
    }

    internal ParsedAutomationBundle ReadDefinitions(AssetPreparationPlan plan)
    {
        var blobs = new ImmutableBlobStore(instance, contracts);
        using var input = blobs.OpenVerified(AssetPlanCodec.Reference(plan.Input));
        if (input.Length > StrictJson.MaximumBytes) throw new IOException("asset_preparation_input_size_invalid");
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        var selected = plan.Roots.ToDictionary(value => Guid.Parse(value.SourceRootId), value => value.Path);
        var parsed = new AutomationBundleCodec(contracts).Decode(bytes, selected, plan.ExpectedCatalogRevision);
        return AutomationTemplateIdentity.Remap(parsed, Guid.Parse(plan.OperationId));
    }

    internal static AssetAcceptanceResult ReadAccepted(SqliteConnection connection, SqliteTransaction transaction, Guid operationId, bool isNew)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT i.journal_id,i.state,i.catalog_revision,i.reason_code,j.plan_path,j.plan_digest FROM automation.automation_imports i " +
            "JOIN main.operation_journals j ON i.journal_id=j.journal_id WHERE i.operation_id=$id AND j.operation_id=$id;";
        command.Parameters.AddWithValue("$id", operationId.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new IOException("asset_acceptance_requires_reconciliation");
        return new(new(operationId, Guid.Parse(reader.GetString(0)), (AutomationImportState)reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2), reader.GetString(3), isNew), reader.GetString(4), reader.GetString(5));
    }

    internal static string Step(string digest) => "asset-" + digest;
}
