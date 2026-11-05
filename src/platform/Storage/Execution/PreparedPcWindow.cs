using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record PcWindowFacts(PcStageBinding Binding,
    TargetExecutionOutcome Ownership, BlobReference ReadyArtifact, ProcessIdentity Process,
    long WindowHandle, int Width, int Height);

[SupportedOSPlatform("windows")]
internal sealed class PreparedPcWindow : IDisposable
{
    private readonly PcTargetStage _owner;
    private int _disposed;
    internal PcWindowFacts Facts { get; }

    internal PreparedPcWindow(PcTargetStage owner, PcWindowFacts facts)
    { _owner = owner; Facts = facts; }

    internal T Use<T>(Func<PcWindowFacts, T> effect)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _owner.UseWindow(this, effect);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _owner.ReleaseWindow(this);
    }
}
