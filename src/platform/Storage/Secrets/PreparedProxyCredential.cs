using System.Runtime.Versioning;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Secrets;

namespace NexusPipeline.Platform.Storage.Secrets;

[SupportedOSPlatform("windows")]
public sealed class PreparedProxyCredential : IDisposable
{
    private readonly ResourceOperationBorrow _borrow;
    private readonly Action<SecretFieldReference, SecretValueReader> _read;
    public Guid InstanceId { get; }
    public Guid OperationId { get; }
    public SecretFieldReference Reference { get; }
    public bool IsConfigured => Reference.SecretId.HasValue;

    internal PreparedProxyCredential(Guid instanceId, Guid operationId, SecretFieldReference reference, ResourceOperationBorrow borrow,
        Action<SecretFieldReference, SecretValueReader> read)
    {
        InstanceId = instanceId; OperationId = operationId; Reference = reference; _borrow = borrow; _read = read;
    }

    public void Use(SecretValueReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _borrow.Use(() => { _read(Reference, reader); return true; });
    }

    public void VerifyHeld(Guid expectedInstanceId, Guid expectedOperationId)
    {
        _borrow.VerifyHeld();
        if (InstanceId != expectedInstanceId || OperationId != expectedOperationId)
            throw new InvalidOperationException("host_secret_reference_unbound");
    }
    public void Dispose() => _borrow.Dispose();
}
