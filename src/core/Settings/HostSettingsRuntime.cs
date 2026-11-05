using System.Collections.Immutable;

namespace NexusPipeline.Core.Settings;

public sealed record SettingsStartupValues(DesktopMode DesktopMode, ServicePreferences Service, PluginSourceChannel PluginSource,
    bool? RegisteredAutoStart = null);
public sealed record SettingsRuntimeSnapshot(HostSettingsSnapshot Saved, HostSettings Effective, ImmutableArray<SettingsRestartReason> RestartReasons)
{
    public StartupEffectStatus? StartupEffect { get; init; }
}

public sealed class HostSettingsRuntime
{
    private readonly object _gate = new();
    private readonly SettingsStartupValues _startup;
    private SettingsRuntimeSnapshot _current;
    private bool _registeredAutoStart;
    private DesktopMode _desktopMode;

    public HostSettingsRuntime(HostSettingsSnapshot saved, SettingsStartupValues startup)
    {
        _startup = startup;
        _registeredAutoStart = startup.RegisteredAutoStart ?? saved.Values.AutoStart;
        _desktopMode = startup.DesktopMode;
        _current = Project(saved);
    }

    public SettingsRuntimeSnapshot Read() { lock (_gate) return _current; }

    public void PublishDesktopMode(DesktopMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new HostSettingsException("settings_desktop_mode_invalid");
        lock (_gate) { _desktopMode = mode; _current = Project(_current.Saved); }
    }

    public void PublishStartupRegistration(bool enabled)
    {
        lock (_gate) { _registeredAutoStart = enabled; _current = Project(_current.Saved); }
    }

    public void PublishSaved(HostSettingsSnapshot saved)
    {
        lock (_gate)
        {
            if (saved.Revision < _current.Saved.Revision) return;
            var next = Project(saved);
            if (saved.Revision == _current.Saved.Revision && !Same(_current.Saved.Values, saved.Values))
                throw new HostSettingsException("settings_revision_content_conflict");
            _current = next;
        }
    }

    private SettingsRuntimeSnapshot Project(HostSettingsSnapshot saved)
    {
        if (saved.Revision is < 0 or > 9007199254740991) throw new HostSettingsException("settings_revision_invalid");
        HostSettingsPolicy.Validate(saved.Values);
        var effective = saved.Values with { AutoStart = _registeredAutoStart, Service = _startup.Service,
            Desktop = saved.Values.Desktop with { Mode = _desktopMode },
            Updates = saved.Values.Updates with { PluginSource = _startup.PluginSource } };
        HostSettingsPolicy.Validate(effective);
        return new(saved, effective, HostSettingsPolicy.Compare(effective, saved.Values).RestartReasons);
    }

    private static bool Same(HostSettings left, HostSettings right) =>
        (left with { Extensions = right.Extensions }) == right && left.Extensions.SequenceEqual(right.Extensions);
}
