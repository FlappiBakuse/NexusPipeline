namespace NexusPipeline.Extensibility.Lifecycle;

/// <summary>Activates only resources registered in the provided lifetime; failure rolls them back.</summary>
public interface IExtension
{
    /// <summary>All registrations must be awaited; cancellation forbids publishing late contributions.</summary>
    ValueTask ActivateAsync(IExtensionScope scope, CancellationToken cancellationToken);
}

/// <summary>Owns an activation's resources without exposing Host services or a DI container.</summary>
public interface IExtensionScope : IAsyncDisposable
{
    /// <summary>Cancelled before resource cleanup; retained tokens remain usable after disposal.</summary>
    CancellationToken Cancellation { get; }

    /// <summary>Transfers cleanup ownership; a late resource is disposed before this operation completes.</summary>
    ValueTask OwnAsync(IAsyncDisposable resource);
}
