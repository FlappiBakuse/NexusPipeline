namespace NexusPipeline.Extensibility.Emulation;

/// <summary>The Host records command state and retains authority over its complete owned process family.</summary>
public sealed record EmulatorCommandResult(string State, int? ExitCode, string StandardOutput, string StandardError, string ActionId);
/// <summary>A current device session is bound to the selected instance generation rather than its display name.</summary>
public sealed record EmulatorAdbSession(string Id, string TargetId, string Endpoint, string DeviceIdentity,
    bool OwnedInstance, EmulatorProcess? Process, string InstanceEvidenceId);
/// <summary>Device identity is rechecked before control and capture.</summary>
public sealed record EmulatorProbe(string State, string SessionId, string DeviceIdentity);
/// <summary>A Host-published image descriptor; no arbitrary output path is exposed.</summary>
public sealed record EmulatorCapture(string State, string MediaId, string ContentType, int Width, int Height);
/// <summary>A new observation of the same selected installation and physical instance generation.</summary>
public sealed record EmulatorRefresh(EmulatorInstallation Installation, EmulatorInstance Instance,
    IReadOnlyList<EmulatorProcess> Processes, string InstanceEvidenceId);

/// <summary>Readonly discovery and bounded enumeration are scoped by the actual Host operation.</summary>
public interface IEmulatorInventory
{
    /// <summary>Returns only captured installations admitted by the current Host inventory scope.</summary>
    ValueTask<IReadOnlyList<EmulatorInstallation>> DiscoverAsync(EmulatorLayout layout, CancellationToken cancellationToken);
    /// <summary>Runs a fixed enumeration command with exact tool identity and bounded output.</summary>
    ValueTask<EmulatorCommandResult> EnumerateAsync(EmulatorInstallation installation, EmulatorCommand command, CancellationToken cancellationToken);
}

/// <summary>All operations remain bound to one selected target and the current Host resource and lifetime authority.</summary>
public interface IEmulatorHost
{
    /// <summary>Admits only the bound tool and supported vendor operation.</summary>
    ValueTask<EmulatorCommandResult> RunAsync(EmulatorTarget target, EmulatorCommand command, CancellationToken cancellationToken);
    /// <summary>Revalidates captured source bytes and the physical instance generation.</summary>
    ValueTask<EmulatorRefresh> RefreshAsync(EmulatorTarget target, CancellationToken cancellationToken);
    /// <summary>Uses the existing Android owner to authenticate the selected device.</summary>
    ValueTask<EmulatorAdbSession> ConnectAsync(EmulatorTarget target, CancellationToken cancellationToken);
    /// <summary>Rechecks the original session rather than accepting any device at the same port.</summary>
    ValueTask<EmulatorProbe> ProbeAsync(EmulatorAdbSession session, CancellationToken cancellationToken);
    /// <summary>Publishes a frame through the actual Host media owner.</summary>
    ValueTask<EmulatorCapture> CaptureAsync(EmulatorAdbSession session, CancellationToken cancellationToken);
    /// <summary>Requests shutdown only for the original authorized instance.</summary>
    ValueTask<bool> ShutdownAsync(EmulatorAdbSession session, CancellationToken cancellationToken);
    /// <summary>Requires an actual offline observation after shutdown.</summary>
    ValueTask<bool> OfflineAsync(EmulatorAdbSession session, CancellationToken cancellationToken);
    /// <summary>Accepts only current opaque evidence for the exact selected process.</summary>
    ValueTask<bool> StopAsync(EmulatorTarget target, string processEvidenceId, CancellationToken cancellationToken);
    /// <summary>Revokes one Host-issued action without acquiring authority over unrelated instances.</summary>
    ValueTask CancelAsync(string actionId, CancellationToken cancellationToken);
}
