namespace NexusPipeline.Core.Extensions;

public sealed class ExtensionActivation
{
    private int _state;
    public Guid ActivationId { get; } = Guid.NewGuid();
    public string ExtensionId { get; }
    public bool IsPublished => Volatile.Read(ref _state) == 1;
    public bool IsAccepting => Volatile.Read(ref _state) != 2;

    public ExtensionActivation(string extensionId)
    {
        ExtensionIdentifiers.Require(extensionId);
        ExtensionId = extensionId;
    }

    public void Publish()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            throw new InvalidOperationException("extension_activation_not_pending");
    }

    public void Revoke() => Interlocked.Exchange(ref _state, 2);
}
