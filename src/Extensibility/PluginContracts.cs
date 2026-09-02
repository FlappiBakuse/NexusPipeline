using NexusPipeline.Models;

namespace NexusPipeline.Extensibility;

/// <summary>宿主内部数据插件能力标记。外部代码插件使用独立的 Plugin API。</summary>
internal interface IPluginCapability
{
}

/// <summary>数据化或 C# 插件提供的脚本 profile 推导能力。</summary>
internal interface IProfileResolver : IPluginCapability
{
    ScriptProfile? Resolve(string rootPath);
}

internal static class PluginCapabilityKeys
{
    public const string Emulator = "emulator";

    public const string ExecutionPreviewClient = "execution-preview-client";
}

/// <summary>专项插件按当前插件文件推导出的运行时配置快照。</summary>
internal sealed class ScriptProfile
{
    public string MainExe { get; set; } = "";

    public string Args { get; set; } = "";

    public string ConfigPath { get; set; } = "";

    public string LogPath { get; set; } = "";

    public string JudgeScript { get; set; } = "";

    public string JudgeScriptLanguage { get; set; } = "javascript";

    /// <summary>插件 manifest 校验过的判断脚本物理路径，供运行时快照与诊断使用。</summary>
    public string JudgeScriptPath { get; set; } = "";

    public string PluginName { get; set; } = "";

    public string PluginVersion { get; set; } = "";
}
