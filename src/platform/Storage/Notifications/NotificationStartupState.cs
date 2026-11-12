namespace NexusPipeline.Platform.Storage.Notifications;

public sealed class NotificationStartupState
{
    private readonly RuntimeInstance _instance;
    private readonly Guid _generation;
    private int _consumed;

    internal NotificationStartupState(RuntimeInstance instance, Guid generation)
    { _instance = instance; _generation = generation; }

    internal void Consume(RuntimeInstance instance, Guid generation)
    {
        if (_instance != instance || _generation != generation || Interlocked.Exchange(ref _consumed, 1) != 0)
            throw new IOException("notification_startup_inventory_unbound");
    }
}
