using System.Text.Json;
using NexusPipeline.Extensibility;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Utilities;

namespace NexusPipeline.Plugins;

/// <summary>PluginManager 的管理投影、历史贡献和前端资源边界。</summary>
internal sealed partial class PluginManager
{
    /// <summary>插件统一元数据投影（专项数据插件 + managed-code 代码插件）。</summary>
    public IReadOnlyList<PluginSummary> PluginSummaries
    {
        get
        {
            lock (_managementSnapshotSync)
            {
                return _pluginSummariesCache ??= BuildPluginSummaries();
            }
        }
    }

    internal string LocalizePluginDisplayName(string pluginName, string fallback, string? locale)
    {
        string lookupName = ResolveLoadedPluginName(pluginName);
        PluginSummary? summary = PluginSummaries.FirstOrDefault(item =>
            string.Equals(item.Name, lookupName, StringComparison.OrdinalIgnoreCase));
        return summary is null
            ? fallback
            : PluginMetadataLocalization.DisplayName(summary.Locales, fallback, locale);
    }

    /// <summary>插件管理投影的当前内存修订号，供宿主缓存和调试观测使用。</summary>
    internal long PluginManagementRevision
    {
        get
        {
            lock (_managementSnapshotSync)
            {
                return _managementRevision;
            }
        }
    }

    /// <summary>插件文件状态或运行时配置发生变化时清除本地投影缓存。</summary>
    internal void InvalidateManagementSnapshot()
    {
        lock (_managementSnapshotSync)
        {
            _managementRevision++;
            _pluginSummariesCache = null;
            _pluginManagementViewsCache = null;
            _managementStateFingerprint = null;
        }
    }

    private IReadOnlyList<PluginSummary> BuildPluginSummaries()
    {
        var list = new List<PluginSummary>();
        foreach (DataSpecializedPlugin plugin in _dataPlugins)
        {
            PluginPresentationMetadata metadata = PluginPresentationMetadataParser.LoadLocal(
                plugin.PluginDirectory,
                plugin.GameName,
                plugin.Version);
            list.Add(new PluginSummary(
                plugin.Name,
                plugin.ArtifactName,
                plugin.DisplayName,
                string.IsNullOrWhiteSpace(metadata.GameName) ? plugin.GameName : metadata.GameName,
                plugin.Description,
                plugin.Version,
                "data-specialized",
                "",
                plugin.CapabilityKeys.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray(),
                plugin.Frontend is not null,
                plugin.Frontend?.ApiVersion ?? "")
            {
                Authors = metadata.Authors,
                Tags = metadata.Tags,
                Homepage = metadata.Homepage,
                CreatedAt = metadata.CreatedAt,
                UpdatedAt = metadata.UpdatedAt,
                Changelog = metadata.Changelog,
                Locales = metadata.Locales,
                HasReadme = metadata.HasReadme,
                Inputs = ReadInputDeclarations(plugin),
                MinHostVersion = plugin.MinHostVersion,
            });
        }
        foreach (ManagedPluginDescriptor plugin in _managedPlugins)
        {
            string artifactName = plugin.Manifest.ArtifactName;
            PluginPresentationMetadata metadata = PluginPresentationMetadataParser.LoadLocal(
                plugin.Directory,
                plugin.Manifest.GameName,
                plugin.Manifest.Version);
            list.Add(new PluginSummary(
                plugin.Manifest.Name,
                artifactName,
                plugin.Manifest.DisplayName,
                string.IsNullOrWhiteSpace(metadata.GameName) ? plugin.Manifest.GameName : metadata.GameName,
                plugin.Manifest.Description,
                plugin.Manifest.Version,
                "managed-code",
                plugin.Manifest.ApiVersion,
                plugin.Manifest.Capabilities.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray(),
                plugin.Manifest.Frontend is not null,
                plugin.Manifest.Frontend?.ApiVersion ?? "")
            {
                Authors = metadata.Authors,
                Tags = metadata.Tags,
                Homepage = metadata.Homepage,
                CreatedAt = metadata.CreatedAt,
                UpdatedAt = metadata.UpdatedAt,
                Changelog = metadata.Changelog,
                Locales = metadata.Locales,
                HasReadme = metadata.HasReadme,
                MinHostVersion = plugin.Manifest.MinHostVersion,
            });
        }
        return list;
    }

    /// <summary>读取专项插件 resolve.json 的用户输入声明；声明无效时投影为空表并记警告（推导期会再次校验并拒绝）。</summary>
    private static IReadOnlyList<PluginInputDeclaration> ReadInputDeclarations(DataSpecializedPlugin plugin)
    {
        if (!plugin.TryReadInputDeclarations(out IReadOnlyList<PluginInputDeclaration>? declarations, out string? error))
        {
            if (error is not null)
            {
                Logger.Warn($"[插件] 插件「{plugin.Name}」resolve.json inputs 声明无效：{error}");
            }
            return Array.Empty<PluginInputDeclaration>();
        }
        return declarations;
    }

    internal IReadOnlyList<PluginInputDeclaration> LocalizeInputDeclarations(
        string pluginName,
        IReadOnlyList<PluginInputDeclaration> declarations,
        string? locale)
    {
        pluginName = ResolveLoadedPluginName(pluginName);
        DataSpecializedPlugin? plugin = _dataPlugins.FirstOrDefault(item =>
            string.Equals(item.Name, pluginName, StringComparison.OrdinalIgnoreCase));
        return plugin?.LocalizeInputDeclarations(declarations, locale) ?? declarations;
    }

    /// <summary>插件管理控制面共享投影；ownership/pending 由同一份快照合并，避免各适配器自行拼装。</summary>
    internal IReadOnlyList<PluginManagementView> PluginManagementViews
    {
        get
        {
            string fingerprint = ReadManagementStateFingerprint();
            lock (_managementSnapshotSync)
            {
                if (!string.Equals(_managementStateFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    _managementRevision++;
                    _pluginSummariesCache = null;
                    _pluginManagementViewsCache = null;
                    _managementStateFingerprint = fingerprint;
                }
                if (_pluginManagementViewsCache is not null)
                {
                    return _pluginManagementViewsCache;
                }

                IReadOnlyDictionary<string, PluginOwnership> ownership = PluginInstallRecovery.ReadOwnership();
                IReadOnlyList<PluginPendingOperation> pending = PluginInstallRecovery.ReadPending();
                _pluginManagementViewsCache = PluginSummaries
                    .Select(summary => PluginManagementView.Create(summary, this, ownership, pending))
                    .ToArray();
                return _pluginManagementViewsCache;
            }
        }
    }

    /// <summary>按请求语言投影控制面插件元数据，确保状态页与插件页使用同一份本地化结果。</summary>
    internal IReadOnlyList<PluginManagementView> GetLocalizedPluginManagementViews(string? locale)
    {
        return PluginManagementViews
            .Select(view => view with
            {
                DisplayName = PluginMetadataLocalization.DisplayName(view.Locales, view.DisplayName, locale),
                GameName = PluginMetadataLocalization.GameName(view.Locales, view.GameName, locale),
                Description = PluginMetadataLocalization.Description(view.Locales, view.Description, locale),
                Tags = PluginMetadataLocalization.Tags(view.Locales, view.Tags, locale),
                Changelog = PluginMetadataLocalization.Changelog(view.Locales, view.Changelog, locale),
            })
            .ToArray();
    }

    private static string ReadManagementStateFingerprint()
    {
        return string.Join(
            "|",
            DescribeStateFile(AppPaths.PluginOwnershipPath),
            DescribeStateFile(AppPaths.PluginPendingPath));
    }

    private static string DescribeStateFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return path + ":missing";
            }
            FileInfo file = new(path);
            return $"{path}:{file.Length}:{file.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            return path + ":unavailable";
        }
    }

    internal IReadOnlyList<PluginUserGlobalManagementRegistration> UserGlobalManagementContributions =>
        _userGlobalManagement.Snapshot();

    internal PluginLocalizationManifest GetPluginLocalization(string pluginName)
    {
        DataSpecializedPlugin? data = _dataPlugins.FirstOrDefault(plugin =>
            string.Equals(plugin.Name, pluginName, StringComparison.OrdinalIgnoreCase));
        if (data is not null)
        {
            return data.Localization;
        }
        ManagedPluginDescriptor? managed = _managedPlugins.FirstOrDefault(plugin =>
            string.Equals(plugin.Manifest.Name, pluginName, StringComparison.OrdinalIgnoreCase));
        return managed?.Manifest.Localization ?? PluginLocalizationManifest.Empty;
    }

    internal bool TryGetUserGlobalManagementContribution(
        string pluginName,
        string contributionId,
        out PluginUserGlobalManagementRegistration? registration) =>
        _userGlobalManagement.TryGet(pluginName, contributionId, out registration);

    internal IReadOnlyList<PluginUserListBadgeRegistration> UserListBadgeContributions =>
        _userListBadges.Snapshot();

    internal IReadOnlyList<PluginUiContributionRegistration> UiContributions =>
        _uiContributions.Snapshot();

    internal bool TryGetUiContribution(
        string pluginName,
        string contributionId,
        out PluginUiContributionRegistration? registration) =>
        _uiContributions.TryGet(pluginName, contributionId, out registration);

    internal IReadOnlyList<PluginWebApiRegistration> WebApiContributions =>
        _webApi.Snapshot();

    internal bool TryGetWebApi(
        string pluginName,
        string method,
        string route,
        out PluginWebApiRegistration? registration) =>
        _webApi.TryGet(pluginName, method, route, out registration);

    internal IReadOnlyList<PluginHistoryContributionRegistration> HistoryContributions =>
        _historyContributions.Snapshot();

    /// <summary>在历史提交前收集已注册插件的展示快照；插件异常或超限只会丢弃该插件的展示内容。</summary>
    internal void EnrichHistory(RunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.StartTime == DateTime.MinValue)
        {
            return;
        }

        var context = new PluginHistoryContext(
            record.Id,
            record.UserId,
            record.UserName,
            record.ScriptInstanceId,
            record.ScriptName,
            record.QueueId,
            record.QueueName,
            record.Mode,
            new DateTimeOffset(record.StartTime),
            record.EndTime.HasValue ? new DateTimeOffset(record.EndTime.Value) : null,
            record.Status);
        var snapshots = new List<PluginHistoryRecord>();
        int totalBytes = 0;
        foreach (PluginHistoryContributionRegistration registration in HistoryContributions)
        {
            PluginHistoryDisplay? display;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                display = registration.Contribution.Handler(context, timeout.Token)
                    .AsTask()
                    .WaitAsync(timeout.Token)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                Logger.Warn($"[插件:{registration.PluginName}] 历史展示贡献执行失败（{registration.Contribution.Id}）：{ex.Message}");
                continue;
            }
            if (!PluginUiValidation.TrySanitizeHistoryDisplay(display, out PluginHistoryDisplay? sanitized, out string error)
                || sanitized is null)
            {
                if (!string.IsNullOrWhiteSpace(error))
                {
                    Logger.Warn($"[插件:{registration.PluginName}] 历史展示贡献无效（{registration.Contribution.Id}）：{error}");
                }
                continue;
            }

            var snapshot = new PluginHistoryRecord
            {
                PluginName = registration.PluginName,
                PluginDisplayName = registration.PluginDisplayName,
                Id = sanitized.Id,
                Title = sanitized.Title,
                LocalizedTitle = ToLocalizedTextRecord(sanitized.LocalizedTitle),
                Order = registration.Contribution.Order,
                Badges = sanitized.Badges?.Select(badge => new PluginHistoryBadgeRecord
                {
                    Label = badge.Label,
                    Tone = badge.Tone,
                    Title = badge.Title,
                    LocalizedLabel = ToLocalizedTextRecord(badge.LocalizedLabel),
                    LocalizedTitle = ToLocalizedTextRecord(badge.LocalizedTitle),
                }).ToList() ?? new List<PluginHistoryBadgeRecord>(),
                Fields = sanitized.Fields?.Select(field => new PluginHistoryFieldRecord
                {
                    Label = field.Label,
                    Value = field.Value,
                    Tone = field.Tone,
                    LocalizedLabel = ToLocalizedTextRecord(field.LocalizedLabel),
                    LocalizedValue = ToLocalizedValueRecord(field.LocalizedValue),
                }).ToList() ?? new List<PluginHistoryFieldRecord>(),
            };
            int bytes;
            try
            {
                bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOpts.Default).Length;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[插件:{registration.PluginName}] 历史展示贡献序列化失败（{registration.Contribution.Id}）：{ex.Message}");
                continue;
            }
            if (bytes > 16 * 1024 || totalBytes + bytes > 64 * 1024)
            {
                Logger.Warn($"[插件:{registration.PluginName}] 历史展示贡献超出大小上限（{registration.Contribution.Id}）");
                continue;
            }
            snapshots.Add(snapshot);
            totalBytes += bytes;
        }
        record.PluginHistory = snapshots;
    }

    internal void LocalizeHistory(RunRecord record, string locale)
    {
        ArgumentNullException.ThrowIfNull(record);
        foreach (PluginHistoryRecord item in record.PluginHistory ?? new List<PluginHistoryRecord>())
        {
            item.PluginDisplayName = LocalizePluginDisplayName(item.PluginName, item.PluginDisplayName, locale);
            PluginLocalizationManifest localization = GetPluginLocalization(item.PluginName);
            if (item.LocalizedTitle is not null)
            {
                item.Title = localization.Resolve(locale, item.LocalizedTitle.Key, item.Title);
            }
            foreach (PluginHistoryBadgeRecord badge in item.Badges ?? new List<PluginHistoryBadgeRecord>())
            {
                if (badge.LocalizedLabel is not null)
                {
                    badge.Label = localization.Resolve(locale, badge.LocalizedLabel.Key, badge.Label);
                }
                if (badge.LocalizedTitle is not null)
                {
                    badge.Title = localization.Resolve(locale, badge.LocalizedTitle.Key, badge.Title);
                }
            }
            foreach (PluginHistoryFieldRecord field in item.Fields ?? new List<PluginHistoryFieldRecord>())
            {
                if (field.LocalizedLabel is not null)
                {
                    field.Label = localization.Resolve(locale, field.LocalizedLabel.Key, field.Label);
                }
                if (field.LocalizedValue is not null)
                {
                    field.Value = localization.Resolve(
                        locale,
                        field.LocalizedValue.Key,
                        field.LocalizedValue.Fallback.Length > 0 ? field.LocalizedValue.Fallback : field.Value,
                        field.LocalizedValue.Args);
                }
            }
        }
    }

    private static PluginLocalizedTextRecord? ToLocalizedTextRecord(PluginLocalizedText? value)
    {
        return value is null ? null : new PluginLocalizedTextRecord { Key = value.Key, Fallback = value.Fallback };
    }

    private static PluginLocalizedValueRecord? ToLocalizedValueRecord(PluginLocalizedValue? value)
    {
        return value is null
            ? null
            : new PluginLocalizedValueRecord
            {
                Key = value.Key,
                Fallback = value.Fallback,
                Args = value.Args is null
                    ? new Dictionary<string, object?>(StringComparer.Ordinal)
                    : new Dictionary<string, object?>(value.Args, StringComparer.Ordinal),
            };
    }

    internal IReadOnlyList<PluginFrontendRuntimeDescriptor> FrontendDescriptors
    {
        get
        {
            var result = new List<PluginFrontendRuntimeDescriptor>();
            foreach (DataSpecializedPlugin plugin in _dataPlugins)
            {
                TryAddFrontendDescriptor(
                    result,
                    plugin.Name,
                    plugin.DisplayName,
                    plugin.Version,
                    plugin.Frontend,
                    plugin.Localization);
            }
            foreach (ManagedPluginDescriptor plugin in _managedPlugins)
            {
                TryAddFrontendDescriptor(
                    result,
                    plugin.Manifest.Name,
                    plugin.Manifest.DisplayName,
                    plugin.Manifest.Version,
                    plugin.Manifest.Frontend,
                    plugin.Manifest.Localization);
            }
            return result.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    internal bool TryResolveFrontendAsset(
        string pluginName,
        string relativePath,
        out string? filePath)
    {
        filePath = null;
        pluginName = ResolveLoadedPluginName(pluginName);
        if (!IsRuntimeEnabled(pluginName)
            || !PluginFrontendManifest.IsPublicFrontendPath(relativePath))
        {
            return false;
        }
        string? pluginDirectory = _dataPlugins
            .FirstOrDefault(plugin => string.Equals(plugin.Name, pluginName, StringComparison.OrdinalIgnoreCase))?.PluginDirectory;
        DataSpecializedPlugin? data = _dataPlugins.FirstOrDefault(plugin =>
            string.Equals(plugin.Name, pluginName, StringComparison.OrdinalIgnoreCase));
        if (data is null)
        {
            pluginDirectory = _managedPlugins.FirstOrDefault(plugin =>
                string.Equals(plugin.Manifest.Name, pluginName, StringComparison.OrdinalIgnoreCase))?.Directory;
        }
        if (string.IsNullOrWhiteSpace(pluginDirectory))
        {
            return false;
        }
        string root = Path.GetFullPath(pluginDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(Path.Combine(pluginDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(candidate)
            || !IsPublicFrontendExtension(Path.GetExtension(candidate)))
        {
            return false;
        }
        filePath = candidate;
        return true;
    }

    private static PluginFrontendRuntimeDescriptor ToFrontendDescriptor(
        string name,
        string displayName,
        string version,
        PluginFrontendManifest frontend,
        PluginLocalizationManifest localization)
    {
        string prefix = "/plugin-assets/" + Uri.EscapeDataString(name) + "/";
        return new PluginFrontendRuntimeDescriptor(
            name,
            displayName,
            version,
            frontend.ApiVersion,
            prefix + frontend.Entry,
            frontend.Styles.Select(style => prefix + style).ToArray(),
            localization.DefaultLocale,
            localization.Resources);
    }

    private void TryAddFrontendDescriptor(
        List<PluginFrontendRuntimeDescriptor> result,
        string name,
        string displayName,
        string version,
        PluginFrontendManifest? frontend,
        PluginLocalizationManifest localization)
    {
        try
        {
            if (!IsRuntimeEnabled(name) || frontend is null)
            {
                return;
            }
            result.Add(ToFrontendDescriptor(name, displayName, version, frontend, localization));
        }
        catch (Exception ex)
        {
            Logger.Warn($"[插件:{name}] 前端运行时清单生成失败：{ex.Message}");
        }
    }

    private static bool IsPublicFrontendExtension(string extension)
    {
        return extension.ToLowerInvariant() is ".js" or ".mjs" or ".css" or ".json"
            or ".svg" or ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".ico"
            or ".woff" or ".woff2";
    }
}
