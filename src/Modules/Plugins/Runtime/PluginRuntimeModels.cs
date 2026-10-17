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

/// <summary>插件统一元数据投影。仅包含真实数据插件和 managed-code 插件。</summary>
internal sealed record PluginSummary(
    string Name,
    string ArtifactName,
    string DisplayName,
    string GameName,
    string Description,
    string Version,
    string Kind,
    string ApiVersion,
    IReadOnlyList<string> Capabilities,
    bool HasFrontend,
    string FrontendApiVersion)
{
    public IReadOnlyList<PluginAuthor> Authors { get; init; } = Array.Empty<PluginAuthor>();

    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    public string Homepage { get; init; } = "";

    public string CreatedAt { get; init; } = "";

    public string UpdatedAt { get; init; } = "";

    public IReadOnlyList<PluginChangelogEntry> Changelog { get; init; } = Array.Empty<PluginChangelogEntry>();

    public IReadOnlyDictionary<string, PluginLocalizedMetadata> Locales { get; init; } =
        new Dictionary<string, PluginLocalizedMetadata>(StringComparer.OrdinalIgnoreCase);

    public bool HasReadme { get; init; }

    /// <summary>数据化专项插件 resolve.json 声明的用户输入变量（managed-code 恒为空）。</summary>
    public IReadOnlyList<PluginInputDeclaration> Inputs { get; init; } = Array.Empty<PluginInputDeclaration>();

    public string MinHostVersion { get; init; } = "0.0.0";
}

internal sealed record PluginFrontendRuntimeDescriptor(
    string Name,
    string DisplayName,
    string Version,
    string FrontendApiVersion,
    string EntryUrl,
    IReadOnlyList<string> StyleUrls,
    string DefaultLocale,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Localization);

internal enum PluginRuntimeState
{
    Discovered,
    Disabled,
    Incompatible,
    Loading,
    Active,
    InitFailed,
    InitTimedOut,
    StartTimedOut,
    StopTimedOut,
    Shutdown,
}

internal enum PluginLifecyclePhase
{
    Initialize,
    Start,
    Stop,
}

internal sealed class PluginLifecycleTimeoutException : TimeoutException
{
    public PluginLifecycleTimeoutException(PluginLifecyclePhase phase)
        : base($"插件生命周期阶段 {phase} 超过 20 秒截止时间")
    {
        Phase = phase;
    }

    public PluginLifecyclePhase Phase { get; }
}
