namespace NexusPipeline.Extensibility.Emulation;

/// <summary>Extension registrations remain unpublished until the installed activation commits.</summary>
public interface IEmulatorRegistration
{
    /// <summary>Registration is limited to providers declared by the original verified manifest.</summary>
    ValueTask RegisterAsync(string providerId, IEmulatorProvider provider, CancellationToken cancellationToken);
}

/// <summary>Vendor semantics use explicit Host inventory and control scopes rather than direct OS access.</summary>
public interface IEmulatorProvider
{
    /// <summary>Produces distinct installation, enumeration and mapping diagnostics.</summary>
    ValueTask<EmulatorDiscoveryResult> DiscoverAsync(IEmulatorInventory inventory, bool supportsAndroid, CancellationToken cancellationToken);
    /// <summary>Resolves an exact selection from the complete captured source set.</summary>
    EmulatorTarget Select(IReadOnlyList<EmulatorSource> sources, string targetId, string endpoint, bool supportsAndroid);
    /// <summary>Creates a session against an already authorized Host target scope.</summary>
    IEmulatorSession Open(IEmulatorHost host, EmulatorTarget target);
}

/// <summary>The owning Host must drain the session before releasing target and activation authority.</summary>
public interface IEmulatorSession
{
    /// <summary>Starts only the selected instance and verifies its actual ADB session.</summary>
    ValueTask<EmulatorAdbSession> StartAsync(CancellationToken cancellationToken);
    /// <summary>Captures only the same live device session.</summary>
    ValueTask<EmulatorCapture> CaptureAsync(CancellationToken cancellationToken);
    /// <summary>Closing a preexisting instance requires explicit selection authority.</summary>
    ValueTask CloseAsync(bool explicitSelection, CancellationToken cancellationToken);
    /// <summary>Cancellation revokes only actions issued by this session.</summary>
    ValueTask CancelAsync(CancellationToken cancellationToken);
}
