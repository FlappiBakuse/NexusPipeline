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
    internal IReadOnlyList<PluginFrontendRuntimeDescriptor> FrontendDescriptors
    {
        get
        {
            var result = new List<PluginFrontendRuntimeDescriptor>();
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
        pluginName = pluginName.Trim();
        if (!IsRuntimeEnabled(pluginName)
            || !PluginFrontendManifest.IsPublicFrontendPath(relativePath))
        {
            return false;
        }
        string? pluginDirectory = _managedPlugins.FirstOrDefault(plugin =>
            string.Equals(plugin.Manifest.Name, pluginName, StringComparison.OrdinalIgnoreCase))?.Directory;
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
