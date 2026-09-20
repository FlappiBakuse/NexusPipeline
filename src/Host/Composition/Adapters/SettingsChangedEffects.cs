using NexusPipeline.Modules.Settings;
using NexusPipeline.Modules.Settings.Contracts;
using NexusPipeline.Modules.Updates;
using NexusPipeline.Platform.Windows;

namespace NexusPipeline.Host.Composition.Adapters;

/// <summary>Settings save side effects, retained in their historical order.</summary>
internal sealed class SettingsChangedEffects : ISettingsChangedEffects
{
    private readonly UpdateAutomationService _updates;
    private readonly HostLifecycleBridge _lifecycle;

    public SettingsChangedEffects(UpdateAutomationService updates, HostLifecycleBridge lifecycle)
    {
        _updates = updates;
        _lifecycle = lifecycle;
    }

    public void Apply(AppSettings previous, AppSettings current)
    {
        if (current.AllowRemoteAccess && !current.LightweightMode)
        {
            FirewallRule.EnsureAllowInbound(current.WebPort);
        }
        WindowsScheduledTaskRegistration.Sync(current.AutoStart);
        _updates.OnSettingsChanged(previous, current);
        _lifecycle.OnSettingsChanged(previous, current);
    }
}
