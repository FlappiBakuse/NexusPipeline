using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Configuration;

public sealed class StoppedConfigurationEditor
{
    internal FrozenJobClosure Closure { get; }
    internal BlobReference Reference { get; }
    internal StoppedConfigurationEditor(FrozenJobClosure closure, BlobReference reference) { Closure = closure; Reference = reference; }
    public bool WasExecuted => Closure.WasExecuted;
    public override string ToString() => nameof(StoppedConfigurationEditor);
}
