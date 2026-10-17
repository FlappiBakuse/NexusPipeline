using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Shared.Logging;
using NexusPipeline.Shared.Serialization;

namespace NexusPipeline.Modules.Plugins;

internal sealed record PluginUiContributionRegistration(
    Guid Token,
    string PluginName,
    string PluginDisplayName,
    PluginUiContribution Contribution);

internal sealed class PluginUiContributionRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, PluginUiContributionRegistration> _registrations = new();

    public IDisposable Register(
        string pluginName,
        string pluginDisplayName,
        PluginUiContribution contribution)
    {
        PluginUiValidation.ValidateContribution(contribution);
        contribution = contribution with { Access = contribution.Access with
        {
            Actions = new System.Collections.ObjectModel.ReadOnlyDictionary<string, PluginOperationAccess>(
                new Dictionary<string, PluginOperationAccess>(contribution.Access.Actions, StringComparer.OrdinalIgnoreCase)),
        } };
        Guid token = Guid.NewGuid();
        lock (_sync)
        {
            if (_registrations.Values.Any(item =>
                string.Equals(item.PluginName, pluginName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Contribution.Id, contribution.Id, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"插件 UI 贡献 ID 重复：{pluginName}/{contribution.Id}");
            }
            _registrations[token] = new PluginUiContributionRegistration(
                token,
                pluginName,
                pluginDisplayName,
                contribution);
        }
        return new CallbackDisposable(() => Remove(token));
    }

    public IReadOnlyList<PluginUiContributionRegistration> Snapshot(string? slot = null)
    {
        lock (_sync)
        {
            IEnumerable<PluginUiContributionRegistration> result = _registrations.Values;
            if (!string.IsNullOrWhiteSpace(slot))
            {
                result = result.Where(item => string.Equals(
                    item.Contribution.Slot,
                    slot,
                    StringComparison.OrdinalIgnoreCase));
            }
            return result
                .OrderBy(item => item.Contribution.Order)
                .ThenBy(item => item.PluginDisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Contribution.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public bool TryGet(
        string pluginName,
        string contributionId,
        out PluginUiContributionRegistration? registration)
    {
        lock (_sync)
        {
            registration = _registrations.Values.FirstOrDefault(item =>
                string.Equals(item.PluginName, pluginName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Contribution.Id, contributionId, StringComparison.OrdinalIgnoreCase));
            return registration is not null;
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
