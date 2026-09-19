using NexusPipeline.Modules.Configuration.Exchange;
using NexusPipeline.Modules.Scripts.Contracts;

namespace NexusPipeline.Host.Composition.Adapters;

internal sealed class ScriptConfigGateAdapter : IScriptConfigGate
{
    public IDisposable? TryAcquire(string scriptId)
    {
        ScriptConfigGate.Lease lease = ScriptConfigGate.Get(scriptId);
        return lease.Wait(0) ? lease : DisposeAndReturnNull(lease);
    }

    private static IDisposable? DisposeAndReturnNull(IDisposable lease)
    {
        lease.Dispose();
        return null;
    }
}
