using NexusPipeline.Models;

namespace NexusPipeline.Extensibility;

/// <summary>宿主内部数据插件能力标记。外部代码插件使用独立的 Plugin API。</summary>
internal interface IPluginCapability
{
}

/// <summary>resolve.json 声明的用户输入变量：脚本实例按声明提供值，推导时以 {"{"}input:名称{"}"} 内联替换。</summary>
internal sealed class PluginInputDeclaration
{
    public string Name { get; init; } = "";

    public string Label { get; init; } = "";

    public string Description { get; init; } = "";

    public string Default { get; init; } = "";

    public bool Required { get; init; }

    public string Pattern { get; init; } = "";
}

/// <summary>数据化或 C# 插件提供的脚本 profile 推导能力。</summary>
internal interface IProfileResolver : IPluginCapability
{
    /// <summary>inputs 为脚本实例保存的用户输入值（key 为声明 name）；null 表示未提供。</summary>
    ScriptProfile? Resolve(string rootPath, IReadOnlyDictionary<string, string>? inputs);
}

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

/// <summary>专项插件按当前插件文件推导出的运行时配置快照。</summary>
internal sealed class ScriptProfile
{
    public string MainExe { get; set; } = "";

    public string Args { get; set; } = "";

    public string ConfigPath { get; set; } = "";

    /// <summary>附加配置路径（resolve.json paths.extraConfigPaths，绝对路径）：仅参与按用户快照交换，
    /// 判定脚本不可见；校验器只读。缺失宽容，不参与存在性校验。</summary>
    public IReadOnlyList<string> ExtraConfigPaths { get; set; } = Array.Empty<string>();

    public string LogPath { get; set; } = "";

    public string JudgeScript { get; set; } = "";

    public string JudgeScriptLanguage { get; set; } = "javascript";

    /// <summary>插件 manifest 校验过的判断脚本物理路径，供运行时快照与诊断使用。</summary>
    public string JudgeScriptPath { get; set; } = "";

    public string PluginName { get; set; } = "";

    public string PluginVersion { get; set; } = "";

    /// <summary>
    /// configPath 模板引用的输入未定时可绑定的候选清单（输入值缺失或指向的目标不存在，且目录内存在
    /// 两个及以上候选；单候选已被自动绑定，零候选保持空）：宿主据此在配置编辑启动时要求用户选择、
    /// 在运行前拒绝启动，避免把残缺的目录型 configPath 整目录采用为快照。
    /// </summary>
    public IReadOnlyList<string> ConfigInputCandidates { get; set; } = Array.Empty<string>();
}
