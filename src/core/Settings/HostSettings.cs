using System.Collections.Immutable;

namespace NexusPipeline.Core.Settings;

public enum DesktopMode { Desktop, Browser }
public enum HostLogLevel { Debug, Info, Warning, Error, Fatal }
public enum HostLocale { ZhCn, EnUs }
public enum ProxyMode { None, System, Http }
public enum UpdateChannel { Stable, Prerelease }
public enum PluginSourceChannel { Stable, Develop }

public sealed record DesktopPreferences(DesktopMode Mode, bool MinimizeToTray, bool OpenOnStartup);
public sealed record ServicePreferences(int WebPort, bool AllowRemoteAccess, bool McpEnabled, bool McpWriteEnabled, int McpPort);
public sealed record ProxyPreferences(ProxyMode Mode, string? Url, string UserName);
public sealed record UpdatePreferences(bool CheckEnabled, bool AutoApplyEnabled, bool PluginAutoUpdateEnabled,
    UpdateChannel Channel, string? SourceUrl, PluginSourceChannel PluginSource);
public sealed record AutomaticRetryPreferences(bool Enabled, int BackoffSeconds);
public sealed record ExtensionPreference(string ExtensionId, bool Enabled);
public sealed record HostSettings(bool AutoStart, DesktopPreferences Desktop, ServicePreferences Service,
    bool AllowConfigRepair, int HistoryRetentionDays, HostLogLevel LogLevel, HostLocale Locale,
    ProxyPreferences Proxy, UpdatePreferences Updates, ImmutableArray<ExtensionPreference> Extensions)
{
    public AutomaticRetryPreferences AutomaticRetry { get; init; } = new(false, 30);

    public static HostSettings Default { get; } = new(false, new(DesktopMode.Desktop, true, false),
        new(58731, false, false, false, 58732), false, 7, HostLogLevel.Info, HostLocale.ZhCn,
        new(ProxyMode.None, null, ""), new(true, false, false, UpdateChannel.Prerelease, null, PluginSourceChannel.Stable), []);
}

public sealed record HostSettingsSnapshot(long Revision, HostSettings Values);
public sealed class HostSettingsException(string code) : Exception(code);
