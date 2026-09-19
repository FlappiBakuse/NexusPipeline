using NexusPipeline.ControlPlane.Http.Services;
using NexusPipeline.Modules.Updates;

namespace NexusPipeline.Host.Composition.Adapters;

internal sealed class HostRestartPortAdapter : IHostRestartPort
{
    private readonly HostLifecycleBridge _lifecycle;

    public HostRestartPortAdapter(HostLifecycleBridge lifecycle)
    {
        _lifecycle = lifecycle;
    }

    public RestartRequestResult Request(string auditSource)
        => _lifecycle.RequestRestart(auditSource);
}
