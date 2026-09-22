namespace NexusPipeline.Modules.Plugins.Contracts;

/// <summary>Frozen task protocol assets. No runtime reads of an updated plugin package.</summary>
internal sealed record TaskProtocolDescriptor(
    string Version,
    string DiscoverScript,
    string ObserveScript,
    string RetryScript,
    TaskReadResource[] ReadResources)
{
    internal TaskDisplaySnapshot? Localization { get; init; }
}

internal sealed record TaskReadResource(string Id, string Source, string Path, string Format, bool Required);
