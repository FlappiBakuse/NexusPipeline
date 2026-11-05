using System.Collections.Immutable;
using NexusPipeline.Core.Extensions;

namespace NexusPipeline.Core.Settings;

public enum SettingsRestartReason { WebListener, DesktopMode, Mcp, PluginSource }
public sealed record SettingsChangeImpact(ImmutableArray<SettingsRestartReason> RestartReasons, bool StartupRegistrationChanged, bool ProxyChanged);

public static class HostSettingsPolicy
{
    public static void Validate(HostSettings value)
    {
        if (value is null || value.Desktop is null || value.Service is null || value.Proxy is null || value.Updates is null || value.AutomaticRetry is null || value.Extensions.IsDefault)
            throw new HostSettingsException("settings_shape_invalid");
        if (!Enum.IsDefined(value.Desktop.Mode) || !Enum.IsDefined(value.LogLevel) || !Enum.IsDefined(value.Locale) ||
            !Enum.IsDefined(value.Proxy.Mode) || !Enum.IsDefined(value.Updates.Channel) || !Enum.IsDefined(value.Updates.PluginSource))
            throw new HostSettingsException("settings_choice_invalid");
        if ((value.Service.WebPort != 0 && value.Service.WebPort is < 1024 or > 65535) || value.Service.McpPort is < 1024 or > 65535 ||
            value.Service.McpEnabled && value.Service.WebPort == value.Service.McpPort)
            throw new HostSettingsException("settings_listener_invalid");
        if (value.HistoryRetentionDays is < 1 or > 180) throw new HostSettingsException("settings_retention_invalid");
        if (value.AutomaticRetry.BackoffSeconds is < 0 or > 86400)
            throw new HostSettingsException("settings_retry_backoff_invalid");
        ValidateProxy(value.Proxy);
        ValidateUrl(value.Updates.SourceUrl, "settings_update_source_invalid");
        if (value.Extensions.Length > 256 || value.Extensions.Any(item => item is null) ||
            value.Extensions.Select(item => item.ExtensionId).Distinct(StringComparer.Ordinal).Count() != value.Extensions.Length)
            throw new HostSettingsException("settings_extension_preferences_invalid");
        foreach (var item in value.Extensions) ExtensionIdentifiers.Require(item.ExtensionId);
    }

    public static HostSettingsSnapshot Next(HostSettingsSnapshot before, long expectedRevision, HostSettings candidate)
    {
        if (before.Revision != expectedRevision || expectedRevision is < 0 or >= 9007199254740991)
            throw new HostSettingsException("settings_revision_conflict");
        Validate(before.Values);
        Validate(candidate);
        return new(checked(before.Revision + 1), candidate with
            { Extensions = candidate.Extensions.OrderBy(value => value.ExtensionId, StringComparer.Ordinal).ToImmutableArray() });
    }

    public static SettingsChangeImpact Compare(HostSettings before, HostSettings after)
    {
        Validate(before);
        Validate(after);
        var restart = ImmutableArray.CreateBuilder<SettingsRestartReason>();
        if (before.Service.WebPort != after.Service.WebPort || before.Service.AllowRemoteAccess != after.Service.AllowRemoteAccess)
            restart.Add(SettingsRestartReason.WebListener);
        if (before.Desktop.Mode != after.Desktop.Mode) restart.Add(SettingsRestartReason.DesktopMode);
        if (before.Service.McpEnabled != after.Service.McpEnabled || before.Service.McpWriteEnabled != after.Service.McpWriteEnabled ||
            before.Service.McpPort != after.Service.McpPort) restart.Add(SettingsRestartReason.Mcp);
        if (before.Updates.PluginSource != after.Updates.PluginSource) restart.Add(SettingsRestartReason.PluginSource);
        return new(restart.ToImmutable(), before.AutoStart != after.AutoStart, before.Proxy != after.Proxy);
    }

    private static void ValidateProxy(ProxyPreferences value)
    {
        if (value.UserName is null || value.UserName.Length > 256 || value.UserName.Any(char.IsControl))
            throw new HostSettingsException("settings_proxy_user_invalid");
        ValidateUrl(value.Url, "settings_proxy_url_invalid");
        if (value.Mode == ProxyMode.Http && value.Url is null) throw new HostSettingsException("settings_proxy_url_required");
    }

    private static void ValidateUrl(string? value, string code)
    {
        if (value is null) return;
        if (value.Length is 0 or > 2048 || value != value.Trim() || value.Any(char.IsControl) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new HostSettingsException(code);
    }
}
