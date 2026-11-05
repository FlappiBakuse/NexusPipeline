using System.Security.Cryptography;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Security;

namespace NexusPipeline.Platform.Storage.Secrets;

public sealed class FrozenSecretPreparation
{
    private readonly byte[] _bytes;
    internal FrozenSecretPreparation(byte[] bytes) { _bytes = bytes.ToArray(); }
    public byte[] Export() => _bytes.ToArray();
    public SecretPreparationPlan ReadWire() => new ContractReader().Read<SecretPreparationPlan>(_bytes, "secret-preparation-plan.schema.json");
}

public sealed class PreparedSecretPayload : IDisposable
{
    private readonly byte[] _ciphertext;
    private readonly IDisposable[] _held;
    private bool _disposed;
    public FrozenSecretPreparation Plan { get; }
    public BlobReference Reference { get; }
    internal SecretFieldRetirement? Retirement { get; }

    internal PreparedSecretPayload(FrozenSecretPreparation plan, BlobReference reference, ProtectedSecretPayload payload,
        SecretFieldRetirement? retirement, IDisposable[] held)
    {
        Plan = plan; Reference = reference; _ciphertext = payload.Ciphertext.ToArray(); Retirement = retirement; _held = held;
    }

    internal ReadOnlySpan<byte> Ciphertext
    {
        get { ObjectDisposedException.ThrowIf(_disposed, this); return _ciphertext; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_ciphertext);
        for (var index = _held.Length - 1; index >= 0; index--) _held[index].Dispose();
        Retirement?.Dispose();
    }
}
