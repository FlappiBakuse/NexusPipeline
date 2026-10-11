using System.Text.RegularExpressions;
using NexusPipeline.Plugin.Abstractions;

namespace NexusPipeline.Modules.Plugins.Runtime;

internal sealed class PluginDashboardCardRegistry
{
    internal sealed record Card(string Owner, PluginDashboardCardDescriptor Descriptor);
    private readonly object _sync = new();
    private readonly Dictionary<string, (Guid Token, Card Card)> _cards = new(StringComparer.Ordinal);
    private long _revision;
    internal long Revision => Interlocked.Read(ref _revision);
    internal event Action? Changed;

    internal void AvailabilityChanged()
    {
        Interlocked.Increment(ref _revision);
        Changed?.Invoke();
    }

    public IDisposable Register(string owner, PluginDashboardCardDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!Regex.IsMatch(owner, "^[a-z0-9][a-z0-9-]{0,63}$")
            || !Regex.IsMatch(descriptor.Id, "^[a-z0-9][a-z0-9-]{0,63}$")
            || string.IsNullOrWhiteSpace(descriptor.TitleKey) || descriptor.TitleKey.Length > 256
            || descriptor.DescriptionKey is null || descriptor.DescriptionKey.Length > 256)
            throw new ArgumentException("Invalid dashboard card descriptor");
        string id = $"plugin:{owner}:{descriptor.Id}";
        Guid token = Guid.NewGuid();
        lock (_sync)
        {
            if (!_cards.TryAdd(id, (token, new(owner, descriptor))))
                throw new InvalidOperationException($"Dashboard card already registered: {id}");
        }
        AvailabilityChanged();
        return new CallbackDisposable(() =>
        {
            bool removed = false;
            lock (_sync)
                if (_cards.TryGetValue(id, out var active) && active.Token == token) removed = _cards.Remove(id);
            if (removed) AvailabilityChanged();
        });
    }

    public Card[] Snapshot() { lock (_sync) return _cards.Values.Select(item => item.Card).ToArray(); }
    public void Clear()
    {
        bool changed;
        lock (_sync) { changed = _cards.Count > 0; _cards.Clear(); }
        if (changed) AvailabilityChanged();
    }
}
