using NexusPipeline.Host.Lifecycle;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.Settings;
using NexusPipeline.Modules.Updates;

namespace NexusPipeline.Host.Composition.Adapters;

/// <summary>
/// One explicit lifecycle callback binding for services created by the composition root.
/// It is deliberately not a service locator or a type-to-delegate dictionary.
/// </summary>
internal sealed class HostLifecycleBridge
{
    private Bootstrap? _bootstrap;

    public void BindOnce(Bootstrap bootstrap)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        if (Interlocked.CompareExchange(ref _bootstrap, bootstrap, null) is not null)
        {
            throw new InvalidOperationException("Host lifecycle bridge is already bound.");
        }
    }

    public bool CanRequestDirectExit(out string reasonCode) => Require().CanRequestDirectExit(out reasonCode);

    public bool TryRequestUpdateExit() => Require().TryRequestUpdateExit();

    public (HostMaintenanceLease? Lease, string? Reason) TryAcquireUpdateMaintenanceLease()
        => Require().TryAcquireUpdateMaintenanceLease();

    public bool TryRequestCompletionExit() => Require().TryRequestCompletionExit();

    public RestartRequestResult RequestRestart(string auditSource) => Require().RequestRestart(auditSource);

    public RestartRequestResult RequestRestartWithLease(HostMaintenanceLease lease, string auditSource)
        => Require().RequestRestartWithLease(lease, auditSource);

    public void OnSettingsChanged(AppSettings previous, AppSettings current)
        => Require().OnSettingsChanged(previous, current);

    private Bootstrap Require()
        => Volatile.Read(ref _bootstrap)
            ?? throw new InvalidOperationException("Host lifecycle bridge has not been bound.");
}
