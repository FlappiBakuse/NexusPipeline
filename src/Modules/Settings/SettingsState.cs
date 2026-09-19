using NexusPipeline.Modules.Settings.Contracts;

namespace NexusPipeline.Modules.Settings;

/// <summary>
/// Settings owned by the Settings module.  The host creates this state once and
/// publishes a new reference only after the durable save has completed.
/// </summary>
internal sealed class SettingsState : ISettingsProvider
{
    public SettingsState(AppSettings current)
    {
        Current = current ?? throw new ArgumentNullException(nameof(current));
    }

    public AppSettings Current { get; private set; }

    /// <summary>Existing clone-on-write synchronization boundary.</summary>
    internal object MutationLock { get; } = new();

    /// <summary>Publishes a candidate after its durable save has succeeded.</summary>
    internal void ReplaceAfterSave(AppSettings candidate)
    {
        Current = candidate ?? throw new ArgumentNullException(nameof(candidate));
    }
}
