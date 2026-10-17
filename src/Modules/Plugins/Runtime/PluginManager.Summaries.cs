using System.Reflection;
using System.Text.Json;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Modules.Configuration.Snapshots;
using NexusPipeline.Modules.History;
using NexusPipeline.Modules.Notifications;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Plugins.DataSpecialized;
using NexusPipeline.Modules.Plugins.Managed;
using NexusPipeline.Modules.Plugins.Repository;
using NexusPipeline.Modules.Plugins;
using NexusPipeline.Modules.Settings.Persistence;
using NexusPipeline.Modules.Settings.Contracts;
using NexusPipeline.Modules.Settings;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Shared.Logging;
using NexusPipeline.Shared.Serialization;
using NexusPipeline.Shared.Common;

namespace NexusPipeline.Modules.Plugins.Runtime;

internal sealed partial class PluginManager : IPluginCapabilityResolver, IPluginAvailability, IUserRunStartingPublisher, IEmulatorSupportProviderResolver, IPluginExecutionProviderResolver, IDisposable
{
    /// <summary>插件统一元数据投影（专项数据插件 + managed-code 代码插件）。</summary>
    public IReadOnlyList<PluginSummary> PluginSummaries
    {
        get
        {
            return _managementSnapshotCache.GetSummaries(BuildPluginSummaries);
        }
    }

    internal string LocalizePluginDisplayName(string pluginName, string fallback, string? locale)
    {
        string lookupName = pluginName.Trim();
        PluginSummary? summary = PluginSummaries.FirstOrDefault(item =>
            string.Equals(item.Name, lookupName, StringComparison.OrdinalIgnoreCase));
        return summary is null
            ? fallback
            : PluginMetadataLocalization.DisplayName(summary.Locales, fallback, locale);
    }

    /// <summary>插件管理投影的当前内存修订号，供宿主缓存和调试观测使用。</summary>
    internal long PluginManagementRevision
    {
        get => _managementSnapshotCache.Revision;
    }

    /// <summary>插件文件状态或运行时配置发生变化时清除本地投影缓存。</summary>
    internal void InvalidateManagementSnapshot()
    {
        _managementSnapshotCache.Invalidate();
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
                false,
                "")
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
        pluginName = pluginName.Trim();
        DataSpecializedPlugin? plugin = _dataPlugins.FirstOrDefault(item =>
            string.Equals(item.Name, pluginName, StringComparison.OrdinalIgnoreCase));
        return plugin?.LocalizeInputDeclarations(declarations, locale) ?? declarations;
    }

    /// <summary>插件管理控制面共享投影；ownership/pending 由同一份快照合并，避免各适配器自行拼装。</summary>
    internal IReadOnlyList<PluginManagementView> PluginManagementViews
    {
        get => _managementSnapshotCache.GetViews(
            ReadManagementStateFingerprint(),
            () =>
            {
                IReadOnlyDictionary<string, PluginOwnership> ownership = PluginInstallRecovery.ReadOwnership();
                IReadOnlyList<PluginPendingOperation> pending = PluginInstallRecovery.ReadPending();
                return PluginSummaries
                    .Select(summary => PluginManagementView.Create(summary, this, ownership, pending))
                    .ToArray();
            });
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

}
