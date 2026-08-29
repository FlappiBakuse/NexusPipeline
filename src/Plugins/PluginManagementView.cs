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
    IReadOnlyList<string> Replaces,
    bool SupportsEmulator,
    bool ConfiguredEnabled,
    bool RuntimeEnabled,
    string State,
    string? Error,
    bool RestartRequired,
    bool HasFrontend,
    string FrontendApiVersion,
    bool FrontendTrusted,
    bool ManagedByStore,
    string InstalledName,
    string InstalledVersion,
    string InstallationSource,
    string PendingAction,
    string PendingVersion)
{
    public static PluginManagementView Create(
        PluginSummary summary,
        PluginManager manager,
        IReadOnlyDictionary<string, PluginOwnership> ownership,
        IReadOnlyList<PluginPendingOperation> pending)
    {
        ownership.TryGetValue(summary.Name, out PluginOwnership? owner);
        PluginPendingOperation? operation = pending.LastOrDefault(item =>
            string.Equals(item.Name, summary.Name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.SourceName, summary.Name, StringComparison.OrdinalIgnoreCase));
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
            summary.Replaces,
            manager.HasCapability(summary.Name, Extensibility.PluginCapabilityKeys.Emulator),
            configuredEnabled,
            runtimeEnabled,
            manager.GetRuntimeState(summary.Name),
            manager.GetRuntimeError(summary.Name),
            configuredEnabled != runtimeEnabled,
            summary.HasFrontend,
            summary.FrontendApiVersion,
            manager.IsFrontendTrusted(summary.Name),
            owner is not null,
            owner?.Name ?? summary.Name,
            owner?.Version ?? summary.Version,
            owner is not null ? "official-store" : "local",
            operation?.Action ?? "",
            operation?.Version ?? "");
    }
}
