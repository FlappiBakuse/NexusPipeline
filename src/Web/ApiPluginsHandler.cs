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
        if (method == "GET" && seg.Length == 3
            && seg[0].Equals("store", StringComparison.OrdinalIgnoreCase)
            && seg[2].Equals("detail", StringComparison.OrdinalIgnoreCase))
        {
            await WriteStoreDetailAsync(context, seg[1]).ConfigureAwait(false);
            return;
        }
        if (method == "GET" && seg.Length == 2
            && seg[1].Equals("detail", StringComparison.OrdinalIgnoreCase))
        {
            await WriteLocalDetailAsync(context, seg[0]).ConfigureAwait(false);
            return;
        }
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
            await HttpHelper.WriteJsonAsync(context, manager.PluginManagementViews).ConfigureAwait(false);
            return;
        }
        if (method != "POST" || seg.Length != 3)
        {
            await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
            return;
        }
        string name = seg[1];
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
                authors = plugin.Authors.Select(author => new { name = author.Name, url = author.Url }),
                tags = plugin.Tags,
                homepage = plugin.Homepage,
                updatedAt = plugin.UpdatedAt,
                hasReadme = plugin.HasReadme,
                changelog = plugin.Changelog.Select(change => new
                {
                    version = change.Version,
                    date = change.Date,
                    items = change.Items,
                }),
            }),
        }).ConfigureAwait(false);
    }

    private static async Task WriteLocalDetailAsync(HttpListenerContext context, string name)
    {
        PluginDetail? detail = await RuntimeContext.Instance
            .Resolve<PluginRepositoryService>()
            .GetLocalDetailAsync(name)
            .ConfigureAwait(false);
        if (detail is null)
        {
            await HttpHelper.WriteJsonAsync(context, new { ok = false, code = "not_found", error = $"插件不存在：{name}" }, 404)
                .ConfigureAwait(false);
            return;
        }
        await HttpHelper.WriteJsonAsync(context, DetailPayload(detail)).ConfigureAwait(false);
    }

    private static async Task WriteStoreDetailAsync(HttpListenerContext context, string name)
    {
        try
        {
            PluginDetail? detail = await RuntimeContext.Instance
                .Resolve<PluginRepositoryService>()
                .GetStoreDetailAsync(name)
                .ConfigureAwait(false);
            if (detail is null)
            {
                await HttpHelper.WriteJsonAsync(context, new { ok = false, code = "not_found", error = $"插件仓库中不存在：{name}" }, 404)
                    .ConfigureAwait(false);
                return;
            }
            await HttpHelper.WriteJsonAsync(context, DetailPayload(detail)).ConfigureAwait(false);
        }
        catch (PluginRepositoryException ex)
        {
            int status = ex.Code is "repository_unavailable" or "catalog_invalid" or "catalog_too_large" ? 502 : 400;
            await HttpHelper.WriteJsonAsync(context, new { ok = false, code = ex.Code, error = ex.Message }, status)
                .ConfigureAwait(false);
        }
    }

    private static object DetailPayload(PluginDetail detail)
    {
        return new
        {
            ok = true,
            name = detail.Name,
            artifactName = detail.ArtifactName,
            displayName = detail.DisplayName,
            gameName = detail.GameName,
            description = detail.Description,
            version = detail.Version,
            kind = detail.Kind,
            apiVersion = detail.ApiVersion,
            capabilities = detail.Capabilities,
            minHostVersion = detail.MinHostVersion,
            installed = detail.Installed,
            installedName = detail.InstalledName,
            installedVersion = detail.InstalledVersion,
            updateAvailable = detail.UpdateAvailable,
            compatible = detail.Compatible,
            compatibilityReason = detail.CompatibilityReason,
            managedByStore = detail.ManagedByStore,
            pendingAction = detail.PendingAction,
            pendingVersion = detail.PendingVersion,
            status = detail.Status,
            configuredEnabled = detail.ConfiguredEnabled,
            runtimeEnabled = detail.RuntimeEnabled,
            runtimeState = detail.RuntimeState,
            error = detail.RuntimeError,
            restartRequired = detail.RestartRequired,
            hasFrontend = detail.HasFrontend,
            frontendApiVersion = detail.FrontendApiVersion,
            authors = detail.Authors.Select(author => new { name = author.Name, url = author.Url }).ToList(),
            tags = detail.Tags,
            homepage = detail.Homepage,
            updatedAt = detail.UpdatedAt,
            hasReadme = detail.HasReadme,
            readmeAvailable = detail.ReadmeMarkdown.Length > 0,
            readmeMarkdown = detail.ReadmeMarkdown,
            readmeError = detail.ReadmeError,
            changelog = detail.Changelog.Select(change => new
            {
                version = change.Version,
                date = change.Date,
                items = change.Items,
            }).ToList(),
        };
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
