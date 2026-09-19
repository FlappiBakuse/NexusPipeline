using NexusPipeline.Modules.Configuration.Exchange;
using NexusPipeline.Modules.Scripts.Contracts;

namespace NexusPipeline.Host.Composition.Adapters;

internal sealed class ScriptConfigGateAdapter : IScriptConfigGate
{
    public IDisposable? TryAcquire(string scriptId)
    {
        ScriptConfigGate.Lease lease = ScriptConfigGate.Get(scriptId);
        return lease.Wait(0) ? new AcquiredLease(lease) : DisposeAndReturnNull(lease);
    }

    /// <summary>将“已取得的门禁”适配为 IDisposable 契约，确保调用方 Dispose 时释放信号量。</summary>
    private sealed class AcquiredLease : IDisposable
    {
        private ScriptConfigGate.Lease? _lease;

        public AcquiredLease(ScriptConfigGate.Lease lease)
        {
            _lease = lease;
        }

        public void Dispose()
        {
            ScriptConfigGate.Lease? lease = Interlocked.Exchange(ref _lease, null);
            if (lease is null)
            {
                return;
            }

            try
            {
                lease.Release();
            }
            finally
            {
                lease.Dispose();
            }
        }
    }

    private static IDisposable? DisposeAndReturnNull(IDisposable lease)
    {
        lease.Dispose();
        return null;
    }
}
