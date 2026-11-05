using NexusPipeline.Extensibility.Emulation;

namespace NexusPipeline.Platform.Extensions;

internal sealed class EmulatorProviderRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<(Guid Activation, string Id), Entry> _entries = [];
    private sealed record Entry(ExtensionScope Scope, string Id, IEmulatorProvider Provider);

    internal IAsyncDisposable Register(ExtensionScope scope, string providerId, IEmulatorProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider); ProjectCompilerRegistry.RequireId(providerId);
        scope.Cancellation.ThrowIfCancellationRequested(); var entry = new Entry(scope, providerId, provider);
        lock (_gate)
            if (_entries.Count >= 64 || !_entries.TryAdd((scope.ActivationId, providerId), entry))
                throw new IOException("emulator_provider_already_registered");
        return new Registration(this, entry);
    }

    internal async ValueTask<T> UseAsync<T>(string extensionId, string providerId,
        Func<IEmulatorProvider, Action, ValueTask<T>> callback, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested(); ProjectCompilerRegistry.RequireId(providerId);
        Entry entry;
        lock (_gate)
        {
            var found = _entries.Values.Where(item => item.Scope.Activation.ExtensionId == extensionId && item.Id == providerId &&
                item.Scope.IsPublished && !item.Scope.Cancellation.IsCancellationRequested).Take(2).ToArray();
            if (found.Length != 1) throw new IOException("emulator_provider_unavailable");
            entry = found[0];
        }
        using var use = entry.Scope.BeginUse();
        void Check()
        {
            cancellation.ThrowIfCancellationRequested(); entry.Scope.Cancellation.ThrowIfCancellationRequested();
            lock (_gate)
                if (!entry.Scope.IsPublished || !_entries.TryGetValue((entry.Scope.ActivationId, providerId), out var current) ||
                    !ReferenceEquals(entry, current)) throw new IOException("emulator_activation_changed");
        }
        Check(); var result = await callback(entry.Provider, Check).ConfigureAwait(false); Check(); return result;
    }

    private sealed class Registration(EmulatorProviderRegistry owner, Entry entry) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            lock (owner._gate)
                if (owner._entries.TryGetValue((entry.Scope.ActivationId, entry.Id), out var current) && ReferenceEquals(entry, current))
                    owner._entries.Remove((entry.Scope.ActivationId, entry.Id));
            return ValueTask.CompletedTask;
        }
    }
}
