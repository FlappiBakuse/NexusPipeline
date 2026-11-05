using NexusPipeline.Core.Execution;

namespace NexusPipeline.Host.Lifecycle;

internal sealed class HostCompletionExit : IHostCompletionExit
{
    private IHostApplicationLifetime? _lifetime;
    internal void Bind(IHostApplicationLifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        if (Interlocked.CompareExchange(ref _lifetime, lifetime, null) is not null)
            throw new InvalidOperationException("completion_host_lifetime_already_bound");
    }
    public void RequestExit() => (Volatile.Read(ref _lifetime) ?? throw new IOException("completion_host_lifetime_unbound")).StopApplication();
}
