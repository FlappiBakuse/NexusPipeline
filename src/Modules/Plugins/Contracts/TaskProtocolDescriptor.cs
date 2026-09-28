namespace NexusPipeline.Modules.Plugins.Contracts;

/// <summary>Frozen task protocol assets. No runtime reads of an updated plugin package.</summary>
internal sealed record TaskProtocolDescriptor(
    string Version,
    string DiscoverScript,
    string ObserveScript,
    string RetryScript,
    TaskReadResource[] ReadResources)
{
    [System.Text.Json.Serialization.JsonInclude]
    public TaskDisplaySnapshot? Localization { get; init; }

    [System.Text.Json.Serialization.JsonInclude]
    public TaskConfigRuleDescriptor[] ConfigRules { get; init; } = [];

    [System.Text.Json.Serialization.JsonInclude]
    public TaskEnvironmentCheckDescriptor[] EnvironmentChecks { get; init; } = [];

    [System.Text.Json.Serialization.JsonInclude]
    public TaskConfigRepairDescriptor[] RepairRules { get; init; } = [];
}

internal sealed record TaskReadResource(string Id, string Source, string Path, string Format, bool Required, string? Sha256 = null)
{
    public IReadOnlyDictionary<string, string> OperationalFields { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}
