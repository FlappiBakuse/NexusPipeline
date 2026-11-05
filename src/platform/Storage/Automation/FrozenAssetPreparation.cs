using System.Text.Json;
using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Platform.Storage.Automation;

public sealed class FrozenAssetPreparation
{
    private readonly byte[] _bytes;
    internal FrozenAssetPreparation(byte[] bytes) => _bytes = bytes.ToArray();
    public AssetPreparationPlan ReadWire() => JsonSerializer.Deserialize<AssetPreparationPlan>(_bytes)!;
    public byte[] Export() => _bytes.ToArray();
}

public sealed class PreparedAssetPreparation : IDisposable
{
    private List<IDisposable>? _held;
    private readonly FrozenAssetPreparation _plan;
    private readonly BlobReference _reference;

    internal PreparedAssetPreparation(FrozenAssetPreparation plan, BlobReference reference, List<IDisposable> held)
        { _plan = plan; _reference = reference; _held = held; }

    public FrozenAssetPreparation Plan
    {
        get { ObjectDisposedException.ThrowIf(Volatile.Read(ref _held) is null, this); return _plan; }
    }

    public BlobReference Reference
    {
        get { ObjectDisposedException.ThrowIf(Volatile.Read(ref _held) is null, this); return _reference; }
    }

    public void Dispose()
    {
        var held = Interlocked.Exchange(ref _held, null);
        if (held is null) return;
        for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose();
    }
}
