using System.Net;
using NexusPipeline.Plugins;
using NexusPipeline.Services;
using NexusPipeline.Utilities;

namespace NexusPipeline.Web;

[ApiRoute("plugins")]
internal static class ApiPluginsHandler
{
    public static async Task Handle(HttpListenerContext context, string method, string[] seg)
    {
        if (method == "GET" && seg.Length == 2 && seg[1].Equals("store", StringComparison.OrdinalIgnoreCase))
        {
            await WriteStoreAsync(context, forceRefresh: false).ConfigureAwait(false);
            return;
        }
        if (method == "POST" && seg.Length == 3
            && seg[1].Equals("store", StringComparison.OrdinalIgnoreCase)
            && seg[2].Equals("refresh", StringComparison.OrdinalIgnoreCase))
        {
            await WriteStoreAsync(context, forceRefresh: true).ConfigureAwait(false);
            return;
        }
        if (method == "POST" && seg.Length == 4
            && seg[1].Equals("store", StringComparison.OrdinalIgnoreCase))
        {
            await HandleStoreOperationAsync(context, seg[2], seg[3].ToLowerInvariant()).ConfigureAwait(false);
            return;
        }
        if (method == "GET" && seg.Length == 1)
        {
            PluginManager manager = RuntimeContext.Instance.Plugins;
            await HttpHelper.WriteJsonAsync(context, manager.PluginSummaries.Select(plugin => new
            {
                plugin.Name,
                artifactName = plugin.ArtifactName,
                plugin.DisplayName,
                gameName = plugin.GameName,
                plugin.Description,
                plugin.Version,
                kind = plugin.Kind,
                apiVersion = plugin.ApiVersion,
                capabilities = plugin.Capabilities,
                configuredEnabled = manager.IsConfiguredEnabled(plugin.Name),
                runtimeEnabled = manager.IsEnabled(plugin.Name),
                state = manager.GetRuntimeState(plugin.Name),
                error = manager.GetRuntimeError(plugin.Name),
                hasFrontend = plugin.HasFrontend,
                frontendApiVersion = plugin.FrontendApiVersion,
                replaces = plugin.Replaces,
                frontendTrusted = manager.IsFrontendTrusted(plugin.Name),
                restartRequired = manager.IsConfiguredEnabled(plugin.Name)
                    != manager.IsEnabled(plugin.Name),
            })).ConfigureAwait(false);
            return;
        }
        if (method != "POST" || seg.Length != 3)
        {
            await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
            return;
        }
        string name = seg[1];
        if (seg[2].Equals("trust-frontend", StringComparison.OrdinalIgnoreCase)
            || seg[2].Equals("revoke-frontend", StringComparison.OrdinalIgnoreCase))
        {
            bool trusted = seg[2].Equals("trust-frontend", StringComparison.OrdinalIgnoreCase);
            PluginManager manager = RuntimeContext.Instance.Plugins;
            if (!manager.SetFrontendTrusted(name, trusted, Audit.Web, out string? trustFailureCode))
            {
                int status = trustFailureCode == "host_maintenance" ? 409 : 404;
                string message = trustFailureCode == "host_maintenance"
                    ? "宿主正在进行维护操作，暂不能修改插件前端信任设置"
                    : $"插件不存在或没有前端模块：{name}";
                await HttpHelper.WriteJsonAsync(
                    context,
                    new { ok = false, code = trustFailureCode ?? "frontend_not_found", error = message },
                    status).ConfigureAwait(false);
                return;
            }
            await HttpHelper.WriteJsonAsync(context, new
            {
                ok = true,
                frontendTrusted = manager.IsFrontendTrusted(name),
                restartRequired = false,
            }).ConfigureAwait(false);
            return;
        }
        // 显式校验 enable/disable，其余字符串 400（此前任意字符串都按 disable 处理）。
        string verb = seg[2].ToLowerInvariant();
        if (verb is not ("enable" or "disable"))
        {
            await HttpHelper.WriteJsonAsync(context, new { error = "操作无效（应为 enable 或 disable）" }, 400).ConfigureAwait(false);
            return;
        }
        bool enabled = verb == "enable";
        PluginManager plugins = RuntimeContext.Instance.Plugins;
        if (!plugins.SetEnabled(name, enabled, Audit.Web, out string? failureCode))
        {
            int status = failureCode == "host_maintenance" ? 409 : 404;
            string message = failureCode == "host_maintenance"
                ? "宿主正在进行维护操作，暂不能修改插件设置"
                : $"插件不存在：{name}";
            await HttpHelper.WriteJsonAsync(
                context,
                new { ok = false, code = failureCode ?? "not_found", error = message, message },
                status).ConfigureAwait(false);
            return;
        }
        await HttpHelper.WriteJsonAsync(context, new
        {
            ok = true,
            configuredEnabled = plugins.IsConfiguredEnabled(name),
            runtimeEnabled = plugins.IsEnabled(name),
            state = plugins.GetRuntimeState(name),
            restartRequired = true,
        }).ConfigureAwait(false);
    }

    private static async Task WriteStoreAsync(HttpListenerContext context, bool forceRefresh)
    {
        PluginStoreSnapshot snapshot = await RuntimeContext.Instance
            .Resolve<PluginRepositoryService>()
            .GetStoreAsync(forceRefresh)
            .ConfigureAwait(false);
        if (!snapshot.Available)
        {
            await HttpHelper.WriteJsonAsync(
                context,
                new
                {
                    ok = false,
                    available = false,
                    code = "repository_unavailable",
                    error = snapshot.Error ?? "插件仓库暂不可用",
                    stale = false,
                    plugins = Array.Empty<object>(),
                },
                502).ConfigureAwait(false);
            return;
        }
        await HttpHelper.WriteJsonAsync(context, new
        {
            ok = true,
            available = true,
            stale = snapshot.Stale,
            fetchedAt = snapshot.FetchedAt.ToString("O"),
            error = snapshot.Error,
            plugins = snapshot.Plugins.Select(plugin => new
            {
                plugin.Name,
                artifactName = plugin.ArtifactName,
                plugin.DisplayName,
                gameName = plugin.GameName,
                plugin.Description,
                plugin.Version,
                kind = plugin.Kind,
                apiVersion = plugin.ApiVersion,
                capabilities = plugin.Capabilities,
                minHostVersion = plugin.MinHostVersion,
                installed = plugin.Installed,
                installedName = plugin.InstalledName,
                installedVersion = plugin.InstalledVersion,
                updateAvailable = plugin.UpdateAvailable,
                compatible = plugin.Compatible,
                compatibilityReason = plugin.CompatibilityReason,
                managedByStore = plugin.ManagedByStore,
                pendingAction = plugin.PendingAction,
                pendingVersion = plugin.PendingVersion,
                status = plugin.Status,
                changelog = plugin.Changelog.Select(change => new
                {
                    version = change.Version,
                    date = change.Date,
                    items = change.Items,
                }),
            }),
        }).ConfigureAwait(false);
    }

    private static async Task HandleStoreOperationAsync(HttpListenerContext context, string name, string action)
    {
        try
        {
            PluginRepositoryService repository = RuntimeContext.Instance.Resolve<PluginRepositoryService>();
            PluginPendingOperation operation = action switch
            {
                "install" => await repository.InstallAsync(name, update: false).ConfigureAwait(false),
                "update" => await repository.InstallAsync(name, update: true).ConfigureAwait(false),
                "uninstall" => await repository.UninstallAsync(name).ConfigureAwait(false),
                _ => throw new PluginRepositoryException("invalid_action", "插件商店操作无效"),
            };
            Audit.Log(Audit.Web, "登记插件商店操作", $"{operation.Action}：{operation.Name} v{operation.Version}");
            await HttpHelper.WriteJsonAsync(context, new
            {
                ok = true,
                pending = true,
                action = operation.Action,
                name = operation.Name,
                version = operation.Version,
                message = "操作已登记，将在下次重启服务时生效",
            }).ConfigureAwait(false);
        }
        catch (PluginRepositoryException ex)
        {
            int status = ex.Code switch
            {
                "repository_unavailable" or "download_failed" or "catalog_invalid" or "catalog_too_large" => 502,
                "not_found" => 404,
                "invalid_name" or "invalid_action" or "invalid_package_url" => 400,
                _ => 409,
            };
            await HttpHelper.WriteJsonAsync(context, new
            {
                ok = false,
                code = ex.Code,
                error = ex.Message,
                message = ex.Message,
            }, status).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Error($"[插件] 商店操作失败：{ex.Message}");
            await HttpHelper.WriteJsonAsync(context, new
            {
                ok = false,
                code = "internal_error",
                error = ex.Message,
                message = ex.Message,
            }, 500).ConfigureAwait(false);
        }
    }
}
