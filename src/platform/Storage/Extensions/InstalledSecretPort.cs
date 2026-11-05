using System.Runtime.Versioning;
using NexusPipeline.Extensibility.Secrets;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class InstalledSecretPort(InstalledExtensionConsumption consumption, IExtensionSecretPort inner) : IExtensionSecretPort
{
    public async ValueTask<SecretFieldState> GetStateAsync(Guid subjectId, string field, CancellationToken cancellationToken)
    {
        using var use = consumption.Begin();
        var value = await inner.GetStateAsync(subjectId, field, cancellationToken).ConfigureAwait(false);
        use.RequireCurrent(); return value;
    }

    public async ValueTask<SecretFieldState> ApplyAsync(Guid operationId, Guid subjectId, string field, long expectedRevision,
        SecretChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        using (change)
        {
            using var use = consumption.Begin();
            return await inner.ApplyAsync(operationId, subjectId, field, expectedRevision, change, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<SecretDraft> ReadAsync(Guid subjectId, string field, CancellationToken cancellationToken)
    {
        using var use = consumption.Begin();
        var value = await inner.ReadAsync(subjectId, field, cancellationToken).ConfigureAwait(false);
        try { use.RequireCurrent(); return value; }
        catch { value.Dispose(); throw; }
    }
}
