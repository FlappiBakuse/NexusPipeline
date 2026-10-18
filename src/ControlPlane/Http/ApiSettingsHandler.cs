using System.Net;
using System.Text.Json.Nodes;
using NexusPipeline.ControlPlane.Http.Services;
using NexusPipeline.Host.Lifecycle;
using NexusPipeline.Modules.Settings.UseCases;
using NexusPipeline.Modules.Settings.Contracts;
using NexusPipeline.Modules.Notifications;
using NexusPipeline.Modules.Settings;
using NexusPipeline.Modules.Updates;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Windows;
using NexusPipeline.Shared.Localization;
using NexusPipeline.Shared.Logging;
using NexusPipeline.Shared.Results;

namespace NexusPipeline.ControlPlane.Http;

[ApiRoute("settings")]
internal static class ApiSettingsHandler
{
    public static async Task Handle(
        HttpListenerContext context,
        string method,
        string[] seg,
        string body,
        SettingsCommands settingsCommands,
        ISettingsProvider settingsProvider,
        OutboundHttpClientProvider outboundHttp,
        IHostRestartPort restartPort,
        IAccessTokenPort accessToken)
    {
        AppSettings settings = settingsProvider.Current;
        if (method == "GET" && seg.Length == 2 && seg[1] == "access-token")
        {
            bool configured = !string.IsNullOrWhiteSpace(settings.AccessToken);
            string? value = null;
            if (configured && !accessToken.TryDecrypt(settings.AccessToken, out value))
            {
                await HttpHelper.WriteJsonAsync(context, new { code = "access_token_read_failed" }, 500, "no-store").ConfigureAwait(false);
                return;
            }
            await HttpHelper.WriteJsonAsync(context, new { configured, value = configured ? value : null }, cacheControl: "no-store").ConfigureAwait(false);
            return;
        }
        if (method == "GET" && seg.Length == 1)
        {
            RemoteAccessAddresses addresses = settings.AllowRemoteAccess
                ? NetInfo.GetRemoteAccessAddresses() : new(null, null);
            Audit.Log(Audit.Web, "查询设置");
            await HttpHelper.WriteJsonAsync(context, new
            {
                settings = MaskedSettings(settings),
                status = new
                {

                    autoStart = WindowsScheduledTaskRegistration.IsRegistered(),
                    remote = new
                    {
                        allowed = settings.AllowRemoteAccess,
                        bound = WebServer.Current?.AllowsRemoteAccess ?? false,
                        localOnly = !settings.AllowRemoteAccess,
                        tokenSet = !string.IsNullOrWhiteSpace(settings.AccessToken),
                        addresses.InternalAddress,
                        addresses.PublicAddress,
                        port = WebServer.Current?.Port ?? settings.WebPort,
                    },
                },
            }).ConfigureAwait(false);
            return;
        }
        if (method == "PUT" && seg.Length == 1)
        {
            JsonNode? node = HttpHelper.ParseBody(body);
            if (node is not JsonObject patch)
            {
                await HttpHelper.ErrorAsync(context, "invalid_request", 400).ConfigureAwait(false);
                return;
            }
            OperationResult<AppSettings> result = settingsCommands.Update(patch);
            if (!result.Succeeded)
            {
                await ApplicationErrorResponse.WriteAsync(context, result.Error!).ConfigureAwait(false);
                return;
            }
            await HttpHelper.WriteJsonAsync(
                context,
                new { ok = true, settings = MaskedSettings(result.Value!) }).ConfigureAwait(false);
            return;
        }
        if (method == "POST" && seg.Length == 2 && seg[1].ToLowerInvariant() == "test")
        {
            await HttpHelper.ErrorAsync(context, "notification_settings_retired", 410).ConfigureAwait(false);
            return;
        }
        if (method == "POST" && seg.Length == 2 && seg[1].ToLowerInvariant() == "restart")
        {
            int newPort = settingsProvider.Current.WebPort;
            RestartRequestResult restart = restartPort.Request(Audit.Web);
            if (!restart.Accepted)
            {
                int status = restart.Code == "operation_forbidden" ? 400 : 409;
                await HttpHelper.ErrorAsync(context, restart.Code, status).ConfigureAwait(false);
                return;
            }
            // 接受重启前已经取得维护租约，后台生命周期不会再重新做一次易竞态的状态检查。
            // 交接标识与当前实例标识供前端确认新实例接管，newPort 只是新实例优先尝试的配置端口。
            await HttpHelper.WriteJsonAsync(context, new
            {
                ok = true,
                newPort,
                handoffId = restart.HandoffId,
                instanceId = HostInstance.Id,
            }).ConfigureAwait(false);
            return;
        }
        if (seg.Length > 1) await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
        else await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
    }

    internal static object MaskedSettings(AppSettings settings)
    {
        return new
        {
            settings.AutoStart,
            settings.MinimizeToTray,
            settings.LightweightMode,
            settings.AllowConfigRepair,
            settings.OpenDesktopOnStartup,
            settings.HistoryRetentionDays,
            settings.WebPort,
            settings.McpEnabled,
            settings.McpWriteEnabled,
            settings.McpPort,
            settings.ProxyMode,
            settings.ProxyUrl,
            settings.ProxyUsername,
            proxyPassword = string.IsNullOrWhiteSpace(settings.ProxyPassword) ? "" : "enc:***",
            settings.LogLevel,
            settings.HostLocale,
            settings.AllowRemoteAccess,
            accessToken = string.IsNullOrWhiteSpace(settings.AccessToken) ? "" : "enc:***",
            settings.UpdateCheckEnabled,
            settings.UpdateAutoApplyEnabled,
            settings.PluginAutoUpdateEnabled,
            settings.UpdateChannel,
            settings.UpdateSourceUrl,
        };
    }

}
