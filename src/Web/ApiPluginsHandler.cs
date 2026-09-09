using System.Net;
using NexusPipeline.Localization;
using NexusPipeline.Plugins;
using NexusPipeline.Services;
using NexusPipeline.Utilities;

namespace NexusPipeline.Web;

[ApiRoute("plugins")]
internal static class ApiPluginsHandler
{
    public static async Task Handle(HttpListenerContext context, string method, string[] seg)
    {
        // WebServer removes only the "api" segment before invoking a handler;
        // seg[0] is therefore the resource name ("plugins").
        if (method == "GET" && seg.Length == 4
            && seg[1].Equals("store", StringComparison.OrdinalIgnoreCase)
            && seg[3].Equals("detail", StringComparison.OrdinalIgnoreCase))
        {
            await WriteStoreDetailAsync(context, seg[2]).ConfigureAwait(false);
            return;
        }
        if (method == "GET" && seg.Length == 3
            && seg[2].Equals("detail", StringComparison.OrdinalIgnoreCase))
        {
            await WriteLocalDetailAsync(context, seg[1]).ConfigureAwait(false);
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
        if (method == "POST" && seg.Length == 3
            && seg[1].Equals("store", StringComparison.OrdinalIgnoreCase)
            && seg[2].Equals("update-all", StringComparison.OrdinalIgnoreCase))
        {
            await UpdateAllStorePluginsAsync(context).ConfigureAwait(false);
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
            await HttpHelper.WriteJsonAsync(
                context,
                manager.PluginManagementViews
                    .Select(view => ManagementPayload(view, context.Request.Locale))
                    .ToArray()).ConfigureAwait(false);
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
            await HttpHelper.ErrorAsync(context, "invalid_action", 400).ConfigureAwait(false);
            return;
        }
        bool enabled = verb == "enable";
        PluginManager plugins = RuntimeContext.Instance.Plugins;
        if (!plugins.SetEnabled(name, enabled, Audit.Web, out string? failureCode))
        {
            int status = failureCode == "host_maintenance" ? 409 : 404;
            await HttpHelper.ErrorAsync(context, failureCode ?? "not_found", status, new { name }).ConfigureAwait(false);
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
            await HttpHelper.ErrorAsync(
                context,
                "repository_unavailable",
                502,
                details: new { available = false, stale = false, plugins = Array.Empty<object>() }).ConfigureAwait(false);
            return;
        }
        await HttpHelper.WriteJsonAsync(context, new
        {
            ok = true,
            available = true,
            stale = snapshot.Stale,
            fetchedAt = snapshot.FetchedAt.ToString("O"),
            staleErrorCode = snapshot.Error is null ? null : "repository_stale",
            plugins = snapshot.Plugins.Select(plugin => StorePayload(plugin, context.Request.Locale)),
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
            await HttpHelper.ErrorAsync(context, "not_found", 404, new { name }).ConfigureAwait(false);
            return;
        }
        await HttpHelper.WriteJsonAsync(context, DetailPayload(detail, context.Request.Locale)).ConfigureAwait(false);
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
                await HttpHelper.ErrorAsync(context, "not_found", 404, new { name }).ConfigureAwait(false);
                return;
            }
            await HttpHelper.WriteJsonAsync(context, DetailPayload(detail, context.Request.Locale)).ConfigureAwait(false);
        }
        catch (PluginRepositoryException ex)
        {
            int status = ex.Code is "repository_unavailable" or "catalog_invalid" or "catalog_too_large" ? 502 : 400;
            await HttpHelper.ErrorAsync(context, ex.Code, status, new { name }).ConfigureAwait(false);
        }
    }

    private static object ManagementPayload(PluginManagementView view, string locale)
    {
        return new
        {
            name = view.Name,
            artifactName = view.ArtifactName,
            displayName = PluginMetadataLocalization.DisplayName(view.Locales, view.DisplayName, locale),
            gameName = PluginMetadataLocalization.GameName(view.Locales, view.GameName, locale),
            description = PluginMetadataLocalization.Description(view.Locales, view.Description, locale),
            version = view.Version,
            kind = view.Kind,
            apiVersion = view.ApiVersion,
            capabilities = view.Capabilities,
            supportsEmulator = view.SupportsEmulator,
            configuredEnabled = view.ConfiguredEnabled,
            runtimeEnabled = view.RuntimeEnabled,
            state = view.State,
            runtimeErrorCode = string.IsNullOrWhiteSpace(view.Error) ? null : "plugin_runtime_error",
            restartRequired = view.RestartRequired,
            hasFrontend = view.HasFrontend,
            frontendApiVersion = view.FrontendApiVersion,
            managedByStore = view.ManagedByStore,
            installedName = view.InstalledName,
            installedVersion = view.InstalledVersion,
            installationSource = view.InstallationSource,
            pendingAction = view.PendingAction,
            pendingVersion = view.PendingVersion,
            authors = view.Authors.Select(author => new { name = author.Name, url = author.Url }),
            tags = PluginMetadataLocalization.Tags(view.Locales, view.Tags, locale),
            homepage = view.Homepage,
            createdAt = view.CreatedAt,
            updatedAt = view.UpdatedAt,
            hasReadme = view.HasReadme,
            changelog = PluginMetadataLocalization.Changelog(view.Locales, view.Changelog, locale)
                .Select(change => new { version = change.Version, date = change.Date, items = change.Items }),
            selfManagedPcLaunch = view.SelfManagedPcLaunch,
            noFreshConfig = view.NoFreshConfig,
            inputs = view.Inputs,
        };
    }

    private static object StorePayload(PluginStoreItem plugin, string locale)
    {
        return new
        {
            name = plugin.Name,
            artifactName = plugin.ArtifactName,
            displayName = PluginMetadataLocalization.DisplayName(plugin.Locales, plugin.DisplayName, locale),
            gameName = PluginMetadataLocalization.GameName(plugin.Locales, plugin.GameName, locale),
            description = PluginMetadataLocalization.Description(plugin.Locales, plugin.Description, locale),
            version = plugin.Version,
            kind = plugin.Kind,
            apiVersion = plugin.ApiVersion,
            capabilities = plugin.Capabilities,
            minHostVersion = plugin.MinHostVersion,
            installed = plugin.Installed,
            installedName = plugin.InstalledName,
            installedVersion = plugin.InstalledVersion,
            updateAvailable = plugin.UpdateAvailable,
            compatible = plugin.Compatible,
            compatibilityCode = plugin.Compatible ? null : "incompatible",
            managedByStore = plugin.ManagedByStore,
            pendingAction = plugin.PendingAction,
            pendingVersion = plugin.PendingVersion,
            status = plugin.Status,
            authors = plugin.Authors.Select(author => new { name = author.Name, url = author.Url }),
            tags = PluginMetadataLocalization.Tags(plugin.Locales, plugin.Tags, locale),
            homepage = plugin.Homepage,
            createdAt = plugin.CreatedAt,
            updatedAt = plugin.UpdatedAt,
            hasReadme = plugin.HasReadme,
            changelog = PluginMetadataLocalization.Changelog(plugin.Locales, plugin.Changelog, locale)
                .Select(change => new { version = change.Version, date = change.Date, items = change.Items }),
        };
    }

    private static object DetailPayload(PluginDetail detail, string locale)
    {
        return new
        {
            ok = true,
            name = detail.Name,
            artifactName = detail.ArtifactName,
            displayName = PluginMetadataLocalization.DisplayName(detail.Locales, detail.DisplayName, locale),
            gameName = PluginMetadataLocalization.GameName(detail.Locales, detail.GameName, locale),
            description = PluginMetadataLocalization.Description(detail.Locales, detail.Description, locale),
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
            compatibilityCode = detail.Compatible ? null : "incompatible",
            managedByStore = detail.ManagedByStore,
            pendingAction = detail.PendingAction,
            pendingVersion = detail.PendingVersion,
            status = detail.Status,
            configuredEnabled = detail.ConfiguredEnabled,
            runtimeEnabled = detail.RuntimeEnabled,
            runtimeState = detail.RuntimeState,
            runtimeErrorCode = string.IsNullOrWhiteSpace(detail.RuntimeError) ? null : "plugin_runtime_error",
            restartRequired = detail.RestartRequired,
            hasFrontend = detail.HasFrontend,
            frontendApiVersion = detail.FrontendApiVersion,
            authors = detail.Authors.Select(author => new { name = author.Name, url = author.Url }).ToList(),
            tags = PluginMetadataLocalization.Tags(detail.Locales, detail.Tags, locale),
            homepage = detail.Homepage,
            createdAt = detail.CreatedAt,
            updatedAt = detail.UpdatedAt,
            hasReadme = detail.HasReadme,
            readmeAvailable = detail.ReadmeMarkdown.Length > 0,
            readmeMarkdown = detail.ReadmeMarkdown,
            readmeErrorCode = string.IsNullOrWhiteSpace(detail.ReadmeError) ? null : "plugin_readme_error",
            changelog = PluginMetadataLocalization.Changelog(detail.Locales, detail.Changelog, locale).Select(change => new
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
            await HttpHelper.ErrorAsync(context, ex.Code, status, new { name });
        }
        catch (Exception ex)
        {
            Logger.Error($"[插件] 商店操作失败：{ex.Message}");
            string traceId = Guid.NewGuid().ToString("N");
            Logger.Error($"[插件] 商店操作失败（追踪 {traceId}）：{ex}");
            await HttpHelper.ErrorAsync(context, "internal_error", 500, new { traceId }).ConfigureAwait(false);
        }
    }

    private static async Task UpdateAllStorePluginsAsync(HttpListenerContext context)
    {
        PluginRepositoryService repository = RuntimeContext.Instance.Resolve<PluginRepositoryService>();
        PluginStoreSnapshot snapshot;
        try
        {
            snapshot = await repository.GetStoreAsync(false).ConfigureAwait(false);
        }
        catch (PluginRepositoryException ex)
        {
            int status = ex.Code is "repository_unavailable" or "catalog_invalid" or "catalog_too_large" ? 502 : 400;
            await HttpHelper.ErrorAsync(context, ex.Code, status).ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            string traceId = Guid.NewGuid().ToString("N");
            Logger.Error($"[插件] 批量更新读取仓库失败（追踪 {traceId}）：{ex}");
            await HttpHelper.ErrorAsync(context, "internal_error", 500, new { traceId }).ConfigureAwait(false);
            return;
        }
        if (!snapshot.Available)
        {
            await HttpHelper.ErrorAsync(context, "repository_unavailable", 502).ConfigureAwait(false);
            return;
        }

        IReadOnlyList<PluginStoreItem> candidates = snapshot.Plugins
            .Where(plugin => plugin.Installed
                && plugin.ManagedByStore
                && plugin.Compatible
                && plugin.UpdateAvailable
                && string.IsNullOrWhiteSpace(plugin.PendingAction))
            .ToArray();
        var updated = new List<object>();
        var failed = new List<object>();
        foreach (PluginStoreItem candidate in candidates)
        {
            try
            {
                PluginPendingOperation operation = await repository.InstallAsync(candidate.Name, update: true).ConfigureAwait(false);
                updated.Add(new { name = operation.Name, version = operation.Version });
            }
            catch (PluginRepositoryException ex)
            {
                failed.Add(new { name = candidate.Name, code = ex.Code, args = new { name = candidate.Name } });
            }
            catch (Exception ex)
            {
                string traceId = Guid.NewGuid().ToString("N");
                Logger.Error($"[插件] 批量更新 {candidate.Name} 失败（追踪 {traceId}）：{ex}");
                failed.Add(new { name = candidate.Name, code = "internal_error", args = new { name = candidate.Name, traceId }, traceId });
            }
        }

        await HttpHelper.WriteJsonAsync(context, new
        {
            ok = true,
            pending = updated.Count > 0,
            eligible = candidates.Count,
            succeeded = updated.Count,
            eligibleCount = candidates.Count,
            succeededCount = updated.Count,
            failedCount = failed.Count,
            restartRequired = updated.Count > 0,
            updated,
            failed,
            results = updated.Concat(failed).ToArray(),
        }).ConfigureAwait(false);
    }
}
