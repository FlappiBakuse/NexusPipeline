using System.Text.Json.Nodes;

namespace NexusPipeline.Plugin.Abstractions;

/// <summary>Optional configuration lifecycle; discovery and staging never execute project programs.</summary>
public interface IPluginProviderConfigurationLifecycle
{
    ValueTask<PluginProviderConfigurationDiscovery> DiscoverAsync(PluginProviderConfigurationRequest request, CancellationToken cancellationToken);
    ValueTask<PluginProviderConfigurationCandidate> StageAsync(PluginProviderConfigurationRequest request, CancellationToken cancellationToken);
    Task<PluginProviderConfigurationInitialization> InitializeAsync(PluginProviderConfigurationContext context, CancellationToken cancellationToken);
    ValueTask CommitAsync(PluginProviderConfigurationCandidate candidate, PluginProviderConfigurationInitialization initialization, CancellationToken cancellationToken);
    ValueTask AbortAsync(PluginProviderConfigurationCandidate candidate, CancellationToken cancellationToken);
}

/// <summary>Prepares locked dependencies under the host's configuration resource lease, without starting project programs.</summary>
public interface IPluginProviderConfigurationPreparation
{
    ValueTask<PluginProviderConfigurationCandidate> PrepareDependenciesAsync(PluginProviderConfigurationCandidate candidate, CancellationToken cancellationToken);
}

/// <summary>Read-only check used after source filesystem changes; never starts project programs.</summary>
public interface IPluginProviderConfigurationSynchronization
{
    ValueTask<PluginProviderConfigurationSourceState> InspectSourceAsync(PluginProviderConfigurationRequest request, CancellationToken cancellationToken);
}
public sealed record PluginProviderConfigurationSourceState(string State, string SourceRevision, string DiagnosticCode,
    IReadOnlyList<string>? WatchFiles = null, IReadOnlyList<string>? WatchDirectories = null);

public sealed record PluginProviderConfigurationRequest(string OperationId, string ProfileId, string ScriptInstanceId,
    string UserId, string PackageRoot, string InterfaceRelativePath, string ExpectedRevision, JsonObject Configuration);
public sealed record PluginProviderConfigurationDiscovery(JsonObject Sources, IReadOnlyList<PluginProviderDiagnostic> Diagnostics);
public sealed record PluginProviderConfigurationCandidate(string OperationId, string ProfileId, string Revision,
    string ExpectedRevision, string AuthorizationFingerprint, IReadOnlyList<PluginProviderResource> Resources,
    JsonObject PrivateCandidate, JsonObject DependencyPlan, PluginProviderConfigurationLaunch? Launch = null);
public sealed record PluginProviderConfigurationLaunch(string Executable, string Arguments, int WaitSeconds, string Sha256);
public sealed record PluginProviderConfigurationContext(PluginProviderConfigurationCandidate Candidate,
    IPluginProviderWorkerPort Worker, Func<PluginProviderEvent, ValueTask> PublishEvent,
    PluginProviderLaunchTarget? LaunchTarget = null);
public sealed record PluginProviderConfigurationInitialization(bool Ready, bool CleanupConfirmed, JsonObject Facts,
    IReadOnlyList<PluginProviderDiagnostic> Diagnostics);
