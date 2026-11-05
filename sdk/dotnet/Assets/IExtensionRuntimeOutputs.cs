namespace NexusPipeline.Extensibility.Assets;

/// <summary>Allocates a new output directory for this activation, without accepting a caller filesystem root.</summary>
public interface IExtensionRuntimeOutputs
{
    /// <summary>A fresh output directory is confined to this instance and activation.</summary>
    ValueTask<IExtensionOutputLease> CreateAsync(string portableName, CancellationToken cancellationToken);
}

/// <summary>Names Host-allocated runtime output. Disposal closes access; it does not authorize recursive deletion.</summary>
public interface IExtensionOutputLease : IAsyncDisposable
{
    /// <summary>The path remains valid only while its original directory identity and scope are current.</summary>
    string Path { get; }
}
