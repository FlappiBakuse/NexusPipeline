using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services.Update;

namespace NexusPipeline.Plugins;

/// <summary>将 catalog、安装所有权和插件摘要投影为商店视图。</summary>
internal sealed class PluginStoreProjector
{
    private readonly Func<PluginManager> _plugins;

    internal PluginStoreProjector(Func<PluginManager> plugins)
    {
        _plugins = plugins;
    }

    internal static bool IsUpdateEligible(PluginStoreItem plugin)
    {
        return plugin.Installed
            && plugin.Compatible
            && plugin.UpdateAvailable
            && string.IsNullOrWhiteSpace(plugin.PendingAction);
    }

    internal static string ResolveStoreStatus(
        PluginCompatibilityResult compatibility,
        bool installed,
        bool updateAvailable,
        bool pending)
    {
        if (pending)
        {
            return "pending";
        }
        if (!compatibility.Compatible
            && compatibility.Code == "host_version_too_low"
            && installed
            && updateAvailable)
        {
            return "update-requires-host-upgrade";
        }
        if (!compatibility.Compatible)
        {
            return "incompatible";
        }
        if (!installed)
        {
            return "not-installed";
        }
        return updateAvailable ? "update-available" : "installed";
    }

    internal PluginStoreSnapshot Project(
        PluginCatalog catalog,
        bool stale,
        DateTimeOffset fetchedAt,
        string? error)
    {
        Dictionary<string, PluginSummary> installed = _plugins().PluginSummaries
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, PluginOwnership> ownership = PluginInstallRecovery.ReadOwnership();
        IReadOnlyList<PluginPendingOperation> pending = PluginInstallRecovery.ReadPending();
        var items = new List<PluginStoreItem>();
        var listedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PluginCatalogEntry entry in catalog.Plugins)
        {
            listedNames.Add(entry.Name);
            installed.TryGetValue(entry.Name, out PluginSummary? local);
            PluginPendingOperation? operation = pending.LastOrDefault(item =>
                string.Equals(item.Name, entry.Name, StringComparison.OrdinalIgnoreCase));
            PluginCompatibilityResult compatibility = PluginRepositoryCatalog.EvaluateCompatibility(
                entry,
                UpdateService.CurrentVersion);
            bool compatible = compatibility.Compatible;
            bool updateAvailable = local is not null
                && PluginRepositoryCatalog.CompareVersions(local.Version, entry.Version) < 0;
            string status = ResolveStoreStatus(
                compatibility,
                installed: local is not null,
                updateAvailable: updateAvailable,
                pending: operation is not null);
            items.Add(new PluginStoreItem(
                entry.Name,
                entry.ArtifactName,
                entry.DisplayName,
                entry.GameName,
                entry.Description,
                entry.Version,
                entry.Kind,
                entry.ApiVersion,
                entry.Capabilities,
                entry.MinHostVersion,
                local is not null,
                local?.Version ?? "",
                updateAvailable,
                compatible,
                compatible ? "" : compatibility.Reason,
                ownership.ContainsKey(entry.Name),
                operation?.Action ?? "",
                operation?.Version ?? "",
                status,
                local?.Name ?? "",
                entry.Changelog)
            {
                Authors = entry.Authors,
                Tags = entry.Tags,
                Homepage = entry.Homepage,
                CreatedAt = entry.CreatedAt,
                UpdatedAt = entry.UpdatedAt,
                Locales = entry.Locales,
                HasReadme = entry.HasReadme,
                CompatibilityCode = compatibility.Code,
            });
        }
        foreach (PluginSummary local in installed.Values
                     .Where(plugin => !listedNames.Contains(plugin.Name))
                     .OrderBy(plugin => plugin.Name, StringComparer.OrdinalIgnoreCase))
        {
            PluginOwnership? localOwnership = ownership.TryGetValue(local.Name, out PluginOwnership? owner)
                ? owner
                : null;
            PluginPendingOperation? operation = pending.LastOrDefault(item =>
                string.Equals(item.Name, local.Name, StringComparison.OrdinalIgnoreCase));
            items.Add(new PluginStoreItem(
                local.Name,
                local.ArtifactName,
                local.DisplayName,
                local.GameName,
                local.Description,
                local.Version,
                local.Kind,
                local.ApiVersion,
                local.Capabilities,
                "0.0.0",
                true,
                local.Version,
                false,
                true,
                "",
                localOwnership is not null,
                operation?.Action ?? "",
                operation?.Version ?? "",
                operation is not null ? "pending" : "unlisted",
                local.Name,
                local.Changelog)
            {
                Authors = local.Authors,
                Tags = local.Tags,
                Homepage = local.Homepage,
                CreatedAt = local.CreatedAt,
                UpdatedAt = local.UpdatedAt,
                Locales = local.Locales,
                HasReadme = local.HasReadme,
            });
        }
        IReadOnlyList<PluginStoreItem> orderedItems = items
            .OrderBy(item => string.Equals(item.Kind, "data-specialized", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new PluginStoreSnapshot(
            true,
            stale,
            fetchedAt,
            error,
            catalog,
            orderedItems);
    }

}
