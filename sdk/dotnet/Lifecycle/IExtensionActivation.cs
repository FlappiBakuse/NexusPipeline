namespace NexusPipeline.Extensibility.Lifecycle;

/// <summary>Separates registration from consumption of the actual published installation.</summary>
public interface IExtensionActivation
{
    /// <summary>Owned background initialization may await this after ActivateAsync returns; cancellation never publishes an installation.</summary>
    ValueTask WaitReadyAsync(CancellationToken cancellationToken);
}
