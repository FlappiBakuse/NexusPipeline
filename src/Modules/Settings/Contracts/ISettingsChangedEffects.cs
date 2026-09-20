using NexusPipeline.Modules.Settings;

namespace NexusPipeline.Modules.Settings.Contracts;

/// <summary>Post-save effects owned by Host composition, not by Settings.</summary>
internal interface ISettingsChangedEffects
{
    void Apply(AppSettings previous, AppSettings current);
}
