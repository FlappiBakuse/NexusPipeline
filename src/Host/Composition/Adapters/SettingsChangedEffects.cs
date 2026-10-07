using NexusPipeline.Modules.Settings;
using NexusPipeline.Modules.Settings.Contracts;
using NexusPipeline.Modules.Updates;
using NexusPipeline.Platform.Windows;

namespace NexusPipeline.Host.Composition.Adapters;

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
        ApplyWindowsChanges(previous, current, FirewallRule.EnsureAllowInbound, WindowsScheduledTaskRegistration.Sync);
        _updates.OnSettingsChanged(previous, current);
        _lifecycle.OnSettingsChanged(previous, current);
    }

    internal static void ApplyWindowsChanges(AppSettings previous, AppSettings current,
        Action<int> ensureInbound, Action<bool> syncStartup)
    {
        if (current.AllowRemoteAccess
            && (!previous.AllowRemoteAccess || previous.WebPort != current.WebPort))
            ensureInbound(current.WebPort);
        if (previous.AutoStart != current.AutoStart) syncStartup(current.AutoStart);
    }
}
