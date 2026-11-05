using System.Text;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Records;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Security;

namespace NexusPipeline.Platform.Storage.Notifications;

internal sealed class NotificationDeliveryCodec(RuntimeInstance instance, ContractReader contracts)
{
    internal NotificationDeliverySnapshot Persist(NotificationDeliveryDocument document)
    {
        Require(document);
        var reference = new ImmutableBlobStore(instance, contracts).PersistPlan(NotificationCatalogCodec.Encode(document));
        return new(document, reference);
    }

    internal NotificationDeliverySnapshot Read(BlobReference reference)
    {
        var document = new NotificationCatalogCodec(instance, contracts).ReadDocument<NotificationDeliveryDocument>(reference);
        Require(document);
        return new(document, reference);
    }

    internal void Require(NotificationDeliveryDocument document)
    {
        if (document.Format != "nexus.notification.delivery" || document.Version != 1 ||
            document.InstanceId != instance.InstanceId || document.OperationId == Guid.Empty ||
            document.AcceptanceId == Guid.Empty || string.IsNullOrWhiteSpace(document.SubjectId) ||
            Encoding.UTF8.GetByteCount(document.SubjectId) > 1024 || document.SubjectId.Contains('\0') ||
            document.Source is null || document.Task is null || document.Task.TaskId == Guid.Empty ||
            document.Task.Revision < 1 || document.CatalogRevision < 1 ||
            document.Secrets.IsDefault || !document.Secrets.Select(item => item.Field).SequenceEqual(Enum.GetValues<NotificationSecretField>()))
            throw new IOException("notification_delivery_document_invalid");
        if ((document.Registration is null) != (document.RouteOrdinal is null) || document.RouteOrdinal is < 0 or > 4095)
            throw new IOException("notification_delivery_registration_unbound");
        if (document.Registration is { } registration) NotificationCatalogCodec.RequirePlan(registration);
        Text(document.Title, 200); Text(document.Body, 4000);
        NotificationCatalogCodec.RequirePlan(document.Catalog);
        NotificationCatalogCodec.RequirePlan(document.Task.Definition);
        if (document.Source.Kind != "executionReceipt")
        {
            if (document.Source.Kind is not ("savedTaskTest" or "scheduledInvocation" or "manualInvocation") || document.Source.Execution is not null ||
                document.Source.ExplicitSource is null || document.Registration is not null || document.RouteOrdinal is not null ||
                document.Attachment is not null) throw new IOException("notification_delivery_source_invalid");
            NotificationCatalogCodec.RequirePlan(document.Source.ExplicitSource);
            RequireSecrets(document.Secrets);
            return;
        }
        if (document.Source.Execution is null || document.Source.ExplicitSource is not null)
            throw new IOException("notification_delivery_source_invalid");
        NotificationCatalogCodec.RequirePlan(document.Source.TotalPlan);
        Content(document.Source.DefinitionBundle);
        var source = document.Source;
        var receipt = source.Receipt;
        if (receipt is null || receipt.Facts is null || receipt.JournalId == Guid.Empty || receipt.OperationId == Guid.Empty ||
            receipt.Facts.RunId == Guid.Empty || receipt.Facts.AttemptId == Guid.Empty || receipt.Facts.Ordinal is < 1 or > 99 ||
            !Enum.IsDefined(receipt.Facts.Engine) || !Enum.IsDefined(receipt.Facts.Business) ||
            !Enum.IsDefined(receipt.Facts.Lifecycle) || !Enum.IsDefined(receipt.Facts.Recovery) ||
            receipt.Facts.Lifecycle is LifecycleOutcome.NotStarted or LifecycleOutcome.Running ||
            source.RouteKind is not ("binding" or "queue") || source.RouteId == Guid.Empty)
            throw new IOException("notification_delivery_source_invalid");
        Digest(receipt.PlanDigest); Digest(receipt.SourceReceiptDigest);
        Text(receipt.Facts.ReasonCode, 80);
        RequireSecrets(document.Secrets);
        if (document.Attachment is { } asset)
        {
            asset.Validate();
            if (asset.RunId != receipt.Facts.RunId || asset.Kind != RecordAssetKind.Screenshot)
                throw new IOException("notification_delivery_attachment_unbound");
        }
    }

    internal static void RequireSecrets(System.Collections.Immutable.ImmutableArray<NotificationDeliverySecret> secrets)
    {
        if (secrets.IsDefault || secrets.Any(item => item is null) ||
            !secrets.Select(item => item.Field).SequenceEqual(Enum.GetValues<NotificationSecretField>()))
            throw new IOException("notification_delivery_secret_invalid");
        foreach (var field in secrets)
        {
            if (field is null) throw new IOException("notification_delivery_secret_invalid");
            if (field.Reference is null || field.Reference.Revision < 0 ||
                field.Reference.Revision > SecretFieldReference.MaximumRevision ||
                (field.Reference.SecretId is null) != (field.Payload is null) ||
                field.Reference.SecretId == Guid.Empty || field.Reference.Revision == 0 && field.Reference.SecretId is not null)
                throw new IOException("notification_delivery_secret_invalid");
            if (field.Payload is not { } payload) continue;
            if (payload.SecretId != field.Reference.SecretId || payload.SizeBytes is < 1 or > 1048576 ||
                payload.Scheme != UserSecretProtector.Scheme || string.IsNullOrEmpty(payload.OwnerSid) ||
                !payload.OwnerSid.StartsWith("S-1-", StringComparison.Ordinal))
                throw new IOException("notification_delivery_secret_invalid");
            Digest(payload.Digest);
        }
    }

    internal static void Content(BlobReference reference)
    {
        if (reference is null || reference.SizeBytes is < 0 or > 1073741824) throw new IOException("notification_delivery_reference_invalid");
        Digest(reference.Sha256);
        if (reference.RelativePath != $"blobs/content/{reference.Sha256}.bin") throw new IOException("notification_delivery_reference_invalid");
    }

    private static void Text(string value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0') || value.Length > maximum)
            throw new IOException("notification_delivery_text_invalid");
    }

    private static void Digest(string value)
    {
        if (value is null || value.Length != 64 || value.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new IOException("notification_delivery_digest_invalid");
    }
}
