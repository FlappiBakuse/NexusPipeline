using NexusPipeline.Modules.Plugins;
namespace NexusPipeline.Modules.Plugins.Repository;

internal sealed record PluginReadmeCacheEntry(
    PluginReadmeResult Result,
    DateTimeOffset LastCheckedAt,
    string? ETag,
    DateTimeOffset? LastModified);
