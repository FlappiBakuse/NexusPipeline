using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Modules.Dashboard;
using NexusPipeline.Modules.Plugins.Runtime;

namespace NexusPipeline.Host.Composition.Adapters;

internal sealed class DashboardCardSourceAdapter(PluginManager plugins) : IDashboardCardSource
{
    public event Action? Changed
    {
        add => plugins.DashboardCardsChanged += value;
        remove => plugins.DashboardCardsChanged -= value;
    }

    public DashboardSourceSnapshot Read(string locale)
    {
        while (true)
        {
        long generation = plugins.DashboardCardRevision;
        var registered = plugins.DashboardCards.OrderBy(c => c.Owner, StringComparer.Ordinal)
            .ThenBy(c => c.Descriptor.Id, StringComparer.Ordinal).ToArray();
        var frontends = plugins.FrontendDescriptors;
        var metadata = registered.Select(card => new
        {
            card.Owner, card.Descriptor,
            Localization = frontends.FirstOrDefault(f => f.Name == card.Owner)?.Localization
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new { Locale = pair.Key, Entries = pair.Value.OrderBy(entry => entry.Key, StringComparer.Ordinal).ToArray() }).ToArray()
        }).ToArray();
        string revision = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Generation = generation, Cards = metadata })));
        var cards = registered.Select(card =>
        {
            var frontend = frontends.FirstOrDefault(descriptor => descriptor.Name == card.Owner);
            string Translate(string key) => frontend is not null && (frontend.Localization.TryGetValue(locale, out var dictionary) || frontend.Localization.TryGetValue(frontend.DefaultLocale, out dictionary))
                && dictionary.TryGetValue(key, out var text) ? text : key;
            return new DashboardCard($"plugin:{card.Owner}:{card.Descriptor.Id}", "plugin", card.Owner, card.Descriptor.Id,
                Translate(card.Descriptor.TitleKey), Translate(card.Descriptor.DescriptionKey), card.Descriptor.DefaultVisible, card.Descriptor.DefaultOrder);
        }).ToArray();
        if (generation == plugins.DashboardCardRevision) return new(revision, cards);
        }
    }
}
