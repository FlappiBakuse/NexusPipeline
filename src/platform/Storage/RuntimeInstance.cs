namespace NexusPipeline.Platform.Storage;

public sealed record RuntimeInstance(Guid InstanceId, string RuntimeRoot, DateTimeOffset CreatedAtUtc)
{
    internal const string DesktopProfileDirectory = "desktop-profile";
}
