using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Extensibility.Secrets;
using NexusPipeline.Extensibility.State;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage.Secrets;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class InstalledSecretDocuments(InstalledExtensionConsumption consumption, SecretDocumentService service,
    ExtensionSecretPort secrets, string extensionId, bool allowed, InstalledWorkEffects? effects = null) : ISecretDocuments
{
    public async ValueTask<DocumentCommit> WriteAsync(Guid operationId, string name, long expectedRevision, ReadOnlyMemory<byte> json,
        IReadOnlyList<DocumentSecretEdit> changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var materials = new List<SecretDocumentEdit>(); byte[]? bytes = null;
        try
        {
            Require(name);
            if (changes.Count > 32 || json.Length is < 1 or > 1048576) throw new ArgumentException("secret_document_request_invalid");
            using var use = await consumption.BeginAsync(cancellationToken).ConfigureAwait(false);
            foreach (var edit in changes)
            {
                if (edit is null || edit.Change is null) throw new ArgumentException("secret_document_edit_invalid");
                _ = secrets.Authorize(edit.SubjectId, edit.Field, SecretAccessKind.Write);
                var disposition = edit.Change.Kind switch
                {
                    SecretChangeKind.Keep => SecretDisposition.Keep,
                    SecretChangeKind.Set => SecretDisposition.Set,
                    SecretChangeKind.Clear => SecretDisposition.Clear,
                    _ => throw new ArgumentException("secret_document_edit_invalid")
                };
                SecretValue? value = null;
                if (disposition == SecretDisposition.Set) edit.Change.UseValue(text => value = new SecretValue(text));
                materials.Add(new(edit.SubjectId, edit.Field, edit.ExpectedRevision, disposition, value));
            }
            bytes = json.ToArray();
            var rotations = effects is null ? [] : await effects.PrepareRotationAsync(materials, cancellationToken).ConfigureAwait(false);
            var result = await service.WriteAsync(operationId, consumption.SubjectId, extensionId, name, expectedRevision, bytes, materials,
                () =>
                {
                    use.RequireCurrent();
                    foreach (var edit in changes) _ = secrets.Authorize(edit.SubjectId, edit.Field, SecretAccessKind.Write);
                }, cancellationToken, consumption.Work?.Activity).ConfigureAwait(false);
            if (effects is not null) await effects.FinishRotationAsync(operationId, rotations, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            foreach (var material in materials) material.Value?.Dispose();
            foreach (var edit in changes) edit?.Change?.Dispose();
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public async ValueTask<DocumentCommit?> ReadCommitAsync(Guid operationId, CancellationToken cancellationToken)
    {
        if (!allowed) throw new UnauthorizedAccessException("extension_instance_data_not_declared");
        if (operationId == Guid.Empty) throw new ArgumentException("extension_document_operation_invalid");
        using var use = await consumption.BeginAsync(cancellationToken).ConfigureAwait(false);
        var result = service.ReadCommit(operationId, extensionId, consumption.SubjectId);
        use.RequireCurrent(); cancellationToken.ThrowIfCancellationRequested(); return result;
    }

    private void Require(string name)
    {
        if (!allowed) throw new UnauthorizedAccessException("extension_instance_data_not_declared");
        ExtensionDataNames.Require(name);
    }
}
