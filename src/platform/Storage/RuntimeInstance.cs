namespace NexusPipeline.Platform.Storage;

public sealed record RuntimeInstance(Guid InstanceId, string RuntimeRoot, DateTimeOffset CreatedAtUtc);
