using System.Net;
using NexusPipeline.Modules.Plugins.Repository;
using NexusPipeline.Modules.Plugins.Runtime;
using NexusPipeline.Modules.Plugins;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Shared.Logging;

namespace NexusPipeline.ControlPlane.Http;

[ApiRoute("plugins")]
internal static class ApiPluginsHandler
{
    public static async Task Handle(
        HttpListenerContext context,
        string method,
        string[] seg,
        PluginManager plugins,
        PluginRepositoryService repository)
    {
        // WebServer removes only the "api" segment before invoking a handler;
        // seg[0] is therefore the resource name ("plugins").
        if (method == "GET" && seg.Length == 4
            && seg[1].Equals("store", StringComparison.OrdinalIgnoreCase)
            && seg[3].Equals("detail", StringComparison.OrdinalIgnoreCase))
        {
            await WriteStoreDetailAsync(context, seg[2], repository).ConfigureAwait(false);
            return;
        }
        if (method == "GET" && seg.Length == 3
            && seg[2].Equals("detail", StringComparison.OrdinalIgnoreCase))
        {
            await WriteLocalDetailAsync(context, seg[1], repository).ConfigureAwait(false);
            return;
        }
        if (method == "GET" && seg.Length == 2 && seg[1].Equals("store", StringComparison.OrdinalIgnoreCase))
        {
            await WriteStoreAsync(context, forceRefresh: false, repository).ConfigureAwait(false);
            return;
        }
        if (method == "POST" && seg.Length == 3
            && seg[1].Equals("store", StringComparison.OrdinalIgnoreCase)
            && seg[2].Equals("refresh", StringComparison.OrdinalIgnoreCase))
        {
            await WriteStoreAsync(context, forceRefresh: true, repository).ConfigureAwait(false);
            return;
        }
        if (method == "POST" && seg.Length == 3
            && seg[1].Equals("store", StringComparison.OrdinalIgnoreCase)
            && seg[2].Equals("update-all", StringComparison.OrdinalIgnoreCase))
        {
            await UpdateAllStorePluginsAsync(context, repository).ConfigureAwait(false);
            return;
        }
        if (method == "POST" && seg.Length == 4
            && seg[1].Equals("store", StringComparison.OrdinalIgnoreCase))
        {
            await HandleStoreOperationAsync(context, seg[2], seg[3].ToLowerInvariant(), repository).ConfigureAwait(false);
            return;
        }
        if (method == "GET" && seg.Length == 1)
        {
            await HttpHelper.WriteJsonAsync(
                context,
                plugins.PluginManagementViews
                    .Select(view => ManagementPayload(plugins, view, context.Request.Locale))
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

    private static async Task WriteStoreAsync(
        HttpListenerContext context,
        bool forceRefresh,
        PluginRepositoryService repository)
    {
        PluginStoreSnapshot snapshot = await repository
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

    private static async Task WriteLocalDetailAsync(
        HttpListenerContext context,
        string name,
        PluginRepositoryService repository)
    {
        PluginDetail? detail = await repository
            .GetLocalDetailAsync(name)
            .ConfigureAwait(false);
        if (detail is null)
        {
            await HttpHelper.ErrorAsync(context, "not_found", 404, new { name }).ConfigureAwait(false);
            return;
        }
        await HttpHelper.WriteJsonAsync(context, DetailPayload(detail, context.Request.Locale)).ConfigureAwait(false);
    }

    private static async Task WriteStoreDetailAsync(
        HttpListenerContext context,
        string name,
        PluginRepositoryService repository)
    {
        try
        {
            PluginDetail? detail = await repository
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

    private static object ManagementPayload(PluginManager manager, PluginManagementView view, string locale)
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
            minHostVersion = view.MinHostVersion,
            capabilities = view.Capabilities,
            supportsEmulator = view.SupportsEmulator,
            configuredEnabled = view.ConfiguredEnabled,
            runtimeEnabled = view.RuntimeEnabled,
            state = view.State,
            compatibilityCode = view.RuntimeErrorCode switch
            {
                "plugin_incompatible_host" => "host_version_too_low",
                "plugin_incompatible_api" => "plugin_api_incompatible",
                _ when string.Equals(view.State, PluginRuntimeState.Incompatible.ToString(), StringComparison.Ordinal)
                    => "incompatible",
                _ => null,
            },
            runtimeErrorCode = string.IsNullOrWhiteSpace(view.Error) ? null : view.RuntimeErrorCode,
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
            inputs = manager.LocalizeInputDeclarations(view.Name, view.Inputs, locale),
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
            compatibilityCode = plugin.CompatibilityCode,
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
            compatibilityCode = DetailCompatibilityCode(detail),
            managedByStore = detail.ManagedByStore,
            pendingAction = detail.PendingAction,
            pendingVersion = detail.PendingVersion,
            status = detail.Status,
            configuredEnabled = detail.ConfiguredEnabled,
            runtimeEnabled = detail.RuntimeEnabled,
            runtimeState = detail.RuntimeState,
            runtimeErrorCode = string.IsNullOrWhiteSpace(detail.RuntimeError) ? null : detail.RuntimeErrorCode,
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

    private static string? DetailCompatibilityCode(PluginDetail detail)
    {
        if (!string.IsNullOrWhiteSpace(detail.CompatibilityCode))
        {
            return detail.CompatibilityCode;
        }
        if (detail.Compatible)
        {
            return null;
        }
        return detail.RuntimeErrorCode switch
        {
            "plugin_incompatible_host" => "host_version_too_low",
            "plugin_incompatible_api" => "plugin_api_incompatible",
            _ => "incompatible",
        };
    }

    private static async Task HandleStoreOperationAsync(
        HttpListenerContext context,
        string name,
        string action,
        PluginRepositoryService repository)
    {
        try
        {
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
                restartRequired = true,
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

    private static async Task UpdateAllStorePluginsAsync(
        HttpListenerContext context,
        PluginRepositoryService repository)
    {
        PluginBatchUpdateResult result;
        try
        {
            result = await repository.UpdateAllAsync().ConfigureAwait(false);
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
        List<object> updated = result.Updated
            .Select(operation => (object)new { name = operation.Name, version = operation.Version })
            .ToList();
        List<object> failed = result.Failed
            .Select(item => (object)new
            {
                name = item.Name,
                code = item.Code,
                args = item.TraceId is null
                    ? (object)new { name = item.Name }
                    : new { name = item.Name, traceId = item.TraceId },
                traceId = item.TraceId,
            })
            .ToList();

        await HttpHelper.WriteJsonAsync(context, new
        {
            ok = true,
            pending = updated.Count > 0,
            eligible = result.Candidates.Count,
            succeeded = updated.Count,
            eligibleCount = result.Candidates.Count,
            succeededCount = updated.Count,
            failedCount = failed.Count,
            restartRequired = updated.Count > 0,
            updated,
            failed,
            results = updated.Concat(failed).ToArray(),
        }).ConfigureAwait(false);
    }
}
