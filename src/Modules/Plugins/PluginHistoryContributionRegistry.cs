using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Shared.Logging;
using NexusPipeline.Shared.Serialization;

namespace NexusPipeline.Modules.Plugins;

internal sealed record PluginHistoryContributionRegistration(
    Guid Token,
    string PluginName,
    string PluginDisplayName,
    PluginHistoryContribution Contribution);

internal sealed class PluginHistoryContributionRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, PluginHistoryContributionRegistration> _registrations = new();

    public IDisposable Register(string pluginName, string pluginDisplayName, PluginHistoryContribution contribution)
    {
        PluginHistoryValidation.Validate(contribution);
        Guid token = Guid.NewGuid();
        lock (_sync)
        {
            if (_registrations.Values.Any(item =>
                string.Equals(item.PluginName, pluginName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Contribution.Id, contribution.Id, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"插件历史贡献 ID 重复：{pluginName}/{contribution.Id}");
            }
            _registrations[token] = new PluginHistoryContributionRegistration(token, pluginName, pluginDisplayName, contribution);
        }
        return new CallbackDisposable(() => Remove(token));
    }

    public IReadOnlyList<PluginHistoryContributionRegistration> Snapshot()
    {
        lock (_sync)
        {
            return _registrations.Values
                .OrderBy(item => item.Contribution.Order)
                .ThenBy(item => item.PluginDisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Contribution.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _registrations.Clear();
        }
    }

    private void Remove(Guid token)
    {
        lock (_sync)
        {
            _registrations.Remove(token);
        }
    }
}
