namespace NexusPipeline.Plugins;

internal sealed record PluginReadmeCacheEntry(
    PluginReadmeResult Result,
    DateTimeOffset LastCheckedAt,
    string? ETag,
    DateTimeOffset? LastModified);
