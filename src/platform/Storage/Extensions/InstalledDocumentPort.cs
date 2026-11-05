using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility.State;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class InstalledDocumentPort(StateDatabase database, ResourceLeaseManager resources,
    InstalledExtensionConsumption consumption, string extensionId, bool allowed) : IExtensionDocuments
{
    private void RequireName(string name)
    {
        if (!allowed) throw new UnauthorizedAccessException("extension_instance_data_not_declared");
        ExtensionDataNames.Require(name);
    }

    public async ValueTask<ExtensionDocument?> ReadAsync(string name, CancellationToken cancellationToken)
    {
        RequireName(name); cancellationToken.ThrowIfCancellationRequested();
        using var use = await consumption.BeginAsync(cancellationToken).ConfigureAwait(false);
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        var result = ExtensionDocumentRows.Read(connection, null, extensionId, name);
        use.RequireCurrent(); cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    public ValueTask<DocumentCommit> WriteAsync(Guid operationId, string name, long expectedRevision,
        ReadOnlyMemory<byte> json, CancellationToken cancellationToken)
    {
        RequireName(name);
        if (operationId == Guid.Empty || expectedRevision is < 0 or > ExtensionDocumentRows.MaximumRevision || json.Length is < 1 or > 1048576)
            throw new ArgumentException("extension_document_write_invalid");
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = json.ToArray();
        try
        {
            using var parsed = StrictJson.Parse(bytes);
            using var use = consumption.Begin();
            var identity = ExtensionDocumentRows.Identity(operationId, consumption.SubjectId, extensionId, name,
                expectedRevision, ExtensionDocumentRows.Digest(bytes));
            var admission = resources.TryAcquire(operationId, [new("extension-data:" + extensionId, ResourceClaimMode.Mutation)]);
            using var mutation = admission.Lease ?? throw new IOException("extension_document_resource_unavailable");
            var result = database.Write((connection, transaction) =>
            {
                use.RequireCurrent(); cancellationToken.ThrowIfCancellationRequested();
                return ExtensionDocumentRows.Write(connection, transaction, identity, extensionId, name,
                    expectedRevision, bytes, ExtensionDocumentRows.Digest(bytes));
            });
            return ValueTask.FromResult(result);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public ValueTask<DocumentCommit?> ReadCommitAsync(Guid operationId, CancellationToken cancellationToken)
    {
        if (!allowed) throw new UnauthorizedAccessException("extension_instance_data_not_declared");
        if (operationId == Guid.Empty) throw new ArgumentException("extension_document_operation_invalid");
        cancellationToken.ThrowIfCancellationRequested();
        using var use = consumption.Begin();
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        var result = ExtensionDocumentRows.ReadCommit(connection, null, operationId, extensionId, consumption.SubjectId);
        use.RequireCurrent(); return ValueTask.FromResult(result);
    }
}
