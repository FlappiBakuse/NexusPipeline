using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

internal sealed class NotificationReceiptCodec(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    internal static Guid OperationId(Guid instanceId, NexusPipeline.Core.Execution.AttemptReceipt receipt) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"notification-receipt/{instanceId:D}/{receipt.Facts.RunId:D}/{receipt.Facts.AttemptId:D}/{receipt.SourceReceiptDigest}")).AsSpan(0, 16));

    internal NotificationReceiptSnapshot Persist(ExecutionNotificationSource source, NotificationCatalogSnapshot catalog,
        ImmutableArray<ReceiptTaskSecrets> tasks, NexusPipeline.Core.Records.RecordAsset? screenshot)
    {
        var document = new NotificationReceiptDocument("nexus.notification.receipt-source", 1, instance.InstanceId,
            OperationId(instance.InstanceId, source.Receipt), source.SubjectId, source.Receipt, source.DeclineReasonCode,
            source.TotalPlan, source.DefinitionBundle, catalog.Reference, catalog.Catalog.Revision, source.Routes.Length, source.Facts, tasks, screenshot,
            new HostSettingsStore(database).Read().Values.Locale);
        Require(document);
        return new(document, new ImmutableBlobStore(instance, contracts).PersistPlan(NotificationCatalogCodec.Encode(document)));
    }

    internal NotificationReceiptSnapshot Read(BlobReference reference, SqliteConnection connection, SqliteTransaction transaction) =>
        ReadContext(reference, connection, transaction).Snapshot;

    internal NotificationReceiptSnapshot ReadMetadata(BlobReference reference, SqliteConnection connection, SqliteTransaction transaction)
    {
        var document = new NotificationCatalogCodec(instance, contracts).ReadDocument<NotificationReceiptDocument>(reference);
        Require(document);
        if (document.InstanceId != instance.InstanceId || database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot) throw Invalid();
        var snapshot = new NotificationReceiptSnapshot(document, reference);
        NotificationReceiptRows.RequireSaved(connection, transaction, snapshot);
        ExecutionNotificationSource.RequirePublishedRecord(connection, transaction, document.Receipt);
        var registered = RecoveryJournalRegistry.ReadInside(connection, transaction, document.Receipt.JournalId);
        if (registered.Phase != JournalPhase.Committed || registered.PlanDigest != document.Receipt.PlanDigest) throw Invalid();
        return snapshot;
    }

    internal ReceiptReadContext ReadContext(BlobReference reference, SqliteConnection connection, SqliteTransaction transaction)
    {
        var snapshot = ReadMetadata(reference, connection, transaction);
        var document = snapshot.Document;
        var registered = RecoveryJournalRegistry.ReadInside(connection, transaction, document.Receipt.JournalId);
        using var history = JournalReadScope.HoldAttempt(instance, contracts, database, registered.JournalId);
        var attemptReference = AttemptPreparedBinding.Reference(instance, registered);
        var owner = database.AttemptReads?.Find(registered.JournalId);
        var metadata = owner?.ReadPlanMetadata(registered, attemptReference);
        var attempt = metadata?.Plan ??
            contracts.Read<NexusPipeline.Platform.Serialization.Protocol.RunAttemptPlan>(Bytes(attemptReference), "run-attempt-plan.schema.json");
        if (attempt.InstanceId != instance.InstanceId.ToString("D") || attempt.AttemptId != document.Receipt.Facts.AttemptId.ToString("D") ||
            attempt.RunId != document.Receipt.Facts.RunId.ToString("D") || attempt.OperationId != document.Receipt.OperationId.ToString("D") ||
            attempt.JournalId != registered.JournalId.ToString("D") || attempt.AttemptOrdinal != document.Receipt.Facts.Ordinal ||
            AttemptPlanCodec.Reference(attempt.ExecutionPlan) != document.TotalPlan) throw Invalid();
        var total = metadata?.Total ??
            contracts.Read<NexusPipeline.Platform.Serialization.Protocol.ExecutionPlan>(Bytes(document.TotalPlan), "execution-plan.schema.json");
        if (total.InstanceId != instance.InstanceId.ToString("D") || total.RunId != attempt.RunId ||
            new BlobReference(total.DefinitionBundle.RelativePath, total.DefinitionBundle.SizeBytes, total.DefinitionBundle.Sha256) != document.DefinitionBundle)
            throw Invalid();
        var physical = new JournalStore(instance, contracts).Read(registered.JournalId);
        if (physical.Revision != registered.Revision || physical.Phase != JournalPhase.Committed ||
            AttemptPreparedBinding.ExpectedReceipt(attempt, physical) != document.Receipt ||
            !ExecutionNotificationSource.OrderedFacts(attempt, total, physical.Root, document.Receipt).SequenceEqual(document.OrderedFacts))
            throw Invalid();
        var bundle = (metadata is null ? null : owner?.ReadDefinitions()) ??
            ExecutionNotificationSource.ReadBundle(instance, contracts, document.DefinitionBundle);
        var routes = ExecutionNotificationSource.ReadRoutes(total, bundle);
        if (routes.Length != document.RouteCount) throw Invalid();
        var catalog = new NotificationCatalogCodec(instance, contracts).Read(document.Catalog);
        if (catalog.Catalog.Revision != document.CatalogRevision) throw Invalid();
        new NotificationCatalogCodec(instance, contracts).RequirePublishedCursor(connection, transaction, catalog);
        var selected = routes.SelectMany(route => route.Selection.TaskIds).ToHashSet();
        var expected = catalog.Catalog.Tasks.Where(task => task.IsEnabled && selected.Contains(task.Id))
            .Select(task => task.Id).OrderBy(id => id.ToString("D"), StringComparer.Ordinal);
        if (!document.Tasks.Select(item => item.TaskId).SequenceEqual(expected)) throw Invalid();
        NotificationReceiptRows.RequireReferences(connection, transaction, snapshot);
        var bindings = bundle.Bindings.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var scripts = bundle.Scripts.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var users = bundle.Users.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var labels = total.Selection.Select(item =>
        {
            if (!bindings.TryGetValue(item.BindingId, out var binding) || !scripts.TryGetValue(binding.ScriptId, out var script) ||
                !users.TryGetValue(binding.UserId, out var user)) throw Invalid();
            return new NotificationBindingLabel(Guid.ParseExact(binding.Id, "D"), script.Name, user.Name,
                item.Disposition, item.ReasonCode, total.Steps.Where(step => step.BindingId == binding.Id).Select(step => step.StepId).ToImmutableArray());
        }).ToImmutableArray();
        return new(snapshot, catalog, routes, labels);
    }

    internal static void Require(NotificationReceiptDocument document)
    {
        var receipt = document.Receipt;
        if (document.Format != "nexus.notification.receipt-source" || document.Version != 1 ||
            document.InstanceId == Guid.Empty || !Enum.IsDefined(document.Locale) || receipt is null || receipt.Facts is null ||
            document.OperationId != OperationId(document.InstanceId, receipt) || document.SubjectId is null ||
            string.IsNullOrWhiteSpace(document.SubjectId) || Encoding.UTF8.GetByteCount(document.SubjectId) > 1024 || document.SubjectId.Contains('\0') ||
            document.RouteCount is < 0 or > 4096 || document.CatalogRevision is < 0 or > 9007199254740991 ||
            document.OrderedFacts.IsDefault || document.OrderedFacts.Length is < 1 or > 4096 ||
            document.OrderedFacts.Any(item => item is null) ||
            document.OrderedFacts.Select(item => item.StepId).Distinct(StringComparer.Ordinal).Count() != document.OrderedFacts.Length ||
            document.Tasks.IsDefault || document.Tasks.Length > 99 ||
            document.Tasks.Any(task => task is null || task.TaskId == Guid.Empty) ||
            document.Tasks.Select(task => task.TaskId).Distinct().Count() != document.Tasks.Length ||
            document.DeclineReasonCode is not (null or "retry_budget_unknown" or "retry_budget_exhausted" or
                "retry_sources_unavailable" or "retry_authority_revoked" or "retry_run_cancelled" or "retry_post_final_not_executed"))
            throw Invalid();
        NotificationCatalogCodec.RequirePlan(document.TotalPlan);
        NotificationCatalogCodec.RequirePlan(document.Catalog);
        NotificationDeliveryCodec.Content(document.DefinitionBundle);
        if (document.Screenshot is { } screenshot)
        {
            screenshot.Validate();
            if (screenshot.RunId != receipt.Facts.RunId || screenshot.Kind != NexusPipeline.Core.Records.RecordAssetKind.Screenshot)
                throw Invalid();
        }
        foreach (var task in document.Tasks) NotificationDeliveryCodec.RequireSecrets(task.Secrets);
        foreach (var fact in document.OrderedFacts)
            if (fact is null || string.IsNullOrWhiteSpace(fact.StepId) || !Enum.IsDefined(fact.Engine) ||
                fact.Engine == NexusPipeline.Core.Execution.EngineOutcome.Running || !Enum.IsDefined(fact.Business) ||
                string.IsNullOrWhiteSpace(fact.ReasonCode) || fact.ReasonCode.Length > 128 ||
                fact.ReasonCode.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_'))) throw Invalid();
    }

    private byte[] Bytes(BlobReference reference)
    {
        if (reference.SizeBytes is < 1 or > StrictJson.MaximumBytes) throw Invalid();
        using var input = new ImmutableBlobStore(instance, contracts).OpenVerified(reference);
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes); return bytes;
    }

    internal static IOException Invalid() => new("notification_receipt_source_unbound");
}
