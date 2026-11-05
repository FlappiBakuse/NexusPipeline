using NexusPipeline.Extensibility.Lifecycle;

namespace Controlled;

public sealed class RetryExtension : IExtension
{
    public RetryExtension(ExtensionActivationContext context) => ArgumentNullException.ThrowIfNull(context);

    public ValueTask ActivateAsync(IExtensionScope scope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}
