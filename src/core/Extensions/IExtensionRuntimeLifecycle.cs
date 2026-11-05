namespace NexusPipeline.Core.Extensions;

public sealed record ExtensionRuntimeState(string ExtensionId, long InstalledRevision, bool IsInstalled,
    bool DesiredEnabled, Guid? ActivationId, string? UnavailableReason)
{
    public string? PackageVersion { get; init; }
    public string? Kind { get; init; }
    public string? ArtifactName { get; init; }
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    public ExtensionMetadata? Metadata { get; init; }
}

public sealed record ExtensionRuntimeSelection(string ExtensionId, long ExpectedInstalledRevision, long ExpectedSettingsRevision);

public interface IExtensionRuntimeLifecycle
{
    IReadOnlyList<ExtensionRuntimeState> ReadStates();
    Task StartAsync(CancellationToken cancellationToken);
    Task<ExtensionRuntimeState> ReconcileAsync(ExtensionRuntimeSelection selection, CancellationToken acceptedHostCancellation);
    Task StopAsync(TimeSpan timeout);
}
