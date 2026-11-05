using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Extensibility.State;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class InstalledImagePort(StateDatabase database, ResourceLeaseManager resources,
    InstalledExtensionConsumption consumption, string extensionId, bool allowed) : IExtensionImages
{
    private void Require()
    { if (!allowed) throw new UnauthorizedAccessException("extension_instance_data_not_declared"); }

    public ValueTask<IReadOnlyList<ExtensionImageInfo>> ListAsync(string documentName, CancellationToken cancellationToken)
    {
        Require(); ExtensionDataNames.Require(documentName); cancellationToken.ThrowIfCancellationRequested();
        using var use = consumption.Begin();
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        _ = ExtensionDocumentRows.Read(connection, null, extensionId, documentName);
        var result = ExtensionImageRows.List(connection, null, extensionId, documentName);
        use.RequireCurrent(); cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(result);
    }

    public ValueTask<ExtensionImageContent> ReadAsync(string imageId, CancellationToken cancellationToken)
    {
        Require(); ExtensionImageBytes.RequireId(imageId); cancellationToken.ThrowIfCancellationRequested();
        using var use = consumption.Begin();
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        var result = ExtensionImageRows.Read(connection, null, extensionId, imageId);
        use.RequireCurrent(); cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(result);
    }

    public ValueTask<DocumentCommit> WriteAsync(Guid operationId, string documentName, long expectedRevision,
        ReadOnlyMemory<byte> json, IReadOnlyList<string> imageIds, ExtensionImageInput? addition,
        CancellationToken cancellationToken)
    {
        Require(); ExtensionDataNames.Require(documentName); cancellationToken.ThrowIfCancellationRequested();
        if (operationId == Guid.Empty || expectedRevision is < 0 or > ExtensionDocumentRows.MaximumRevision ||
            json.Length is < 1 or > 1048576 || imageIds.Count > 32)
            throw new ArgumentException("extension_image_write_invalid");
        var ids = imageIds.ToArray();
        foreach (var id in ids) ExtensionImageBytes.RequireId(id);
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length) throw new ArgumentException("extension_image_duplicate_reference");
        Array.Sort(ids, StringComparer.Ordinal);
        var bytes = json.ToArray(); var imageBytes = addition?.Content.ToArray();
        try
        {
            using var parsed = StrictJson.Parse(bytes);
            var image = imageBytes is null ? null : new ExtensionImageContent(
                ExtensionImageBytes.Inspect(addition!.ContentType, imageBytes), imageBytes);
            if (image is not null && !ids.Contains(image.Info.Id, StringComparer.Ordinal))
                throw new ArgumentException("extension_image_addition_unreferenced");
            using var use = consumption.Begin();
            var digest = ExtensionDocumentRows.Digest(JsonSerializer.SerializeToUtf8Bytes(new
            {
                extensionId, name = documentName, expectedRevision, sha256 = ExtensionDocumentRows.Digest(bytes),
                images = ids, addition = image?.Info
            }));
            var identity = new OperationIdentity(operationId, consumption.SubjectId, digest, "extension.document.write");
            var admission = resources.TryAcquire(operationId, [new("extension-data:" + extensionId, ResourceClaimMode.Mutation)]);
            using var mutation = admission.Lease ?? throw new IOException("extension_document_resource_unavailable");
            var result = database.Write((connection, transaction) =>
            {
                use.RequireCurrent(); cancellationToken.ThrowIfCancellationRequested();
                return ExtensionImageRows.Write(connection, transaction, identity, extensionId, documentName,
                    expectedRevision, bytes, ids, image);
            });
            return ValueTask.FromResult(result);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (imageBytes is not null) CryptographicOperations.ZeroMemory(imageBytes);
        }
    }
}
