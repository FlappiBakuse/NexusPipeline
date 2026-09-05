namespace NexusPipeline.Plugins;

/// <summary>插件管理控制面共享投影。Web、MCP 和其他控制面适配器均从此投影读取插件状态。</summary>
internal sealed record PluginManagementView(
    string Name,
    string ArtifactName,
    string DisplayName,
    string GameName,
    string Description,
    string Version,
    string Kind,
    string ApiVersion,
    IReadOnlyList<string> Capabilities,
    bool SupportsEmulator,
    bool ConfiguredEnabled,
    bool RuntimeEnabled,
    string State,
    string? Error,
    bool RestartRequired,
    bool HasFrontend,
    string FrontendApiVersion,
    bool ManagedByStore,
    string InstalledName,
    string InstalledVersion,
    string InstallationSource,
    string PendingAction,
    string PendingVersion)
{
    public IReadOnlyList<PluginAuthor> Authors { get; init; } = Array.Empty<PluginAuthor>();

    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    public string Homepage { get; init; } = "";

    public string UpdatedAt { get; init; } = "";

    public IReadOnlyList<PluginChangelogEntry> Changelog { get; init; } = Array.Empty<PluginChangelogEntry>();

    public bool HasReadme { get; init; }

    /// <summary>声明 self-managed-pc-launch 能力：PC 客户端启动由脚本自身管理，前端据此禁用游戏启动填写项。</summary>
    public bool SelfManagedPcLaunch { get; init; }

    /// <summary>声明 no-fresh-config 能力：脚本无法生成全新配置文件，前端据此禁用首次编辑的「全新配置文件」入口。</summary>
    public bool NoFreshConfig { get; init; }

    /// <summary>数据化专项插件 resolve.json 声明的用户输入变量（前端专项弹窗表单据此渲染）。</summary>
    public IReadOnlyList<Extensibility.PluginInputDeclaration> Inputs { get; init; } = Array.Empty<Extensibility.PluginInputDeclaration>();

    public static PluginManagementView Create(
        PluginSummary summary,
        PluginManager manager,
        IReadOnlyDictionary<string, PluginOwnership> ownership,
        IReadOnlyList<PluginPendingOperation> pending)
    {
        ownership.TryGetValue(summary.Name, out PluginOwnership? owner);
        PluginPendingOperation? operation = pending.LastOrDefault(item =>
            string.Equals(item.Name, summary.Name, StringComparison.OrdinalIgnoreCase));
        bool configuredEnabled = manager.IsConfiguredEnabled(summary.Name);
        bool runtimeEnabled = manager.IsEnabled(summary.Name);
        return new PluginManagementView(
            summary.Name,
            summary.ArtifactName,
            summary.DisplayName,
            summary.GameName,
            summary.Description,
            summary.Version,
            summary.Kind,
            summary.ApiVersion,
            summary.Capabilities,
            manager.HasCapability(summary.Name, Extensibility.PluginCapabilityKeys.Emulator),
            configuredEnabled,
            runtimeEnabled,
            manager.GetRuntimeState(summary.Name),
            manager.GetRuntimeError(summary.Name),
            configuredEnabled != runtimeEnabled,
            summary.HasFrontend,
            summary.FrontendApiVersion,
            owner is not null,
            owner?.Name ?? summary.Name,
            owner?.Version ?? summary.Version,
            owner is not null ? "official-store" : "local",
            operation?.Action ?? "",
            operation?.Version ?? "")
        {
            Authors = summary.Authors,
            Tags = summary.Tags,
            Homepage = summary.Homepage,
            UpdatedAt = summary.UpdatedAt,
            Changelog = summary.Changelog,
            HasReadme = summary.HasReadme,
            SelfManagedPcLaunch = manager.HasCapability(summary.Name, Extensibility.PluginCapabilityKeys.SelfManagedPcLaunch),
            NoFreshConfig = manager.HasCapability(summary.Name, Extensibility.PluginCapabilityKeys.NoFreshConfig),
            Inputs = summary.Inputs,
        };
    }
}
