using System.Text.Json.Nodes;

namespace NexusPipeline.Plugin.Abstractions;

/// <summary>Only the owning enabled managed plugin can register its provider.</summary>
public interface IPluginExecutionProviderRegistry
{
    IDisposable Register(IPluginExecutionProvider provider);
    /// <summary>Uses Host's existing edit/resource/recovery gate. Null means busy or unavailable.</summary>
    IDisposable? TryAcquireConfiguration(string scriptInstanceId, string userId, string packageRoot);
}

/// <summary>
/// A provider supplies a frozen plan to the existing Host execution lifecycle.
/// None of these methods receives internal Host repositories or a service locator.
/// </summary>
public interface IPluginExecutionProvider
{
    string Id { get; }

    ValueTask<PluginProviderInspection> InspectAsync(
        PluginProviderInspectRequest request, CancellationToken cancellationToken);

    ValueTask<PluginProviderPlan> PrepareAsync(
        PluginProviderPrepareRequest request, CancellationToken cancellationToken);

    Task<PluginProviderRunResult> RunAsync(
        PluginProviderRunContext context, CancellationToken cancellationToken);
}

public sealed record PluginProviderInspectRequest(
    string ProfileId,
    string PackageRoot,
    string InterfaceRelativePath,
    string ConfigRevision);

public sealed record PluginProviderInspection(
    string ProjectName,
    string ProjectVersion,
    IReadOnlyList<PluginProviderDiagnostic> Diagnostics,
    JsonObject PublicSchema);

public sealed record PluginProviderDiagnostic(string Code, string Message, string Severity, string? Field = null);

public sealed record PluginProviderPrepareRequest(
    string ProfileId,
    string UserId,
    string ScriptInstanceId,
    string PackageRoot,
    string InterfaceRelativePath,
    string ConfigRevision,
    JsonObject Configuration);

public sealed record PluginProviderResource(string Kind, string Identity);

public sealed record PluginProviderTask(string Id, string Name, int Order);

public sealed record PluginProviderPlan(
    string PlanId,
    string ConfigRevision,
    string AuthorizationFingerprint,
    IReadOnlyList<PluginProviderResource> Resources,
    IReadOnlyList<PluginProviderTask> Tasks,
    JsonObject PrivatePlan);

/// <summary>Host-mediated worker launcher: provider never owns queue/history/lease state.</summary>
public interface IPluginProviderWorkerPort
{
    Task<PluginProviderWorkerResult> RunAsync(
        PluginProviderWorkerRequest request,
        Func<PluginProviderEvent, ValueTask> onEvent,
        CancellationToken cancellationToken);
}

public sealed record PluginProviderWorkerRequest(
    string RelativeExecutable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    JsonObject Input);

public sealed record PluginProviderWorkerResult(string ExitKind, int? ExitCode, bool CleanupConfirmed);

public sealed record PluginProviderEvent(
    string Kind,
    long Sequence,
    string? TaskId,
    string? Status,
    JsonObject Evidence);

public sealed record PluginProviderRunContext(
    string ExecutionId,
    string RecordId,
    int AttemptNumber,
    string UserId,
    string ScriptInstanceId,
    PluginProviderPlan Plan,
    IPluginProviderWorkerPort Worker,
    Func<PluginProviderEvent, ValueTask> PublishEvent,
    PluginProviderLaunchTarget? LaunchTarget = null);

/// <summary>Strong target facts captured after the existing Host launch controller confirms readiness.</summary>
public sealed record PluginProviderLaunchTarget(
    string Kind, string Identity, int ProcessId = 0, long WindowHandle = 0, DateTime? StartedAtUtc = null);

public sealed record PluginProviderRunResult(string EngineStatus, string Detail, bool WorkerCleanupConfirmed);
