namespace NexusPipeline.Modules.Plugins.Contracts;


internal static class PluginCapabilityKeys
{
    public const string Emulator = "emulator";

    public const string ExecutionPreviewClient = "execution-preview-client";

    /// <summary>PC 客户端启动由脚本自身（含其启动器）管理：外部不代填游戏路径/启动参数/等待秒数。</summary>
    public const string SelfManagedPcLaunch = "self-managed-pc-launch";

    /// <summary>声明 no-fresh-config 能力：脚本没有生成全新配置文件的能力（配置由目标软件自建），
    /// 首次编辑配置时禁用「全新配置文件」入口，仅允许复用现有配置。</summary>
    public const string NoFreshConfig = "no-fresh-config";
}
