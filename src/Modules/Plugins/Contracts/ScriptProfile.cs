using NexusPipeline.Modules.Plugins.DataSpecialized;
using NexusPipeline.Platform.Processes;
namespace NexusPipeline.Modules.Plugins.Contracts;


/// <summary>专项插件按当前插件文件推导出的运行时配置快照。</summary>
internal sealed class ScriptProfile
{
    public ProcessRole RootProcessRole { get; set; } = ProcessRole.AutomationWorker;
    public string OutputEncoding { get; set; } = "";
    public TaskProtocolDescriptor? TaskProtocol { get; set; }

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

    /// <summary>ConfigInputCandidates 对应的真实 inputs 名称。</summary>
    public string ConfigInputName { get; set; } = "";

    /// <summary>
    /// configPath 模板引用的输入未定时可绑定的候选清单（输入值缺失或指向的目标不存在，且目录内存在
    /// 两个及以上候选；单候选已被自动绑定，零候选保持空）：宿主据此在配置编辑启动时要求用户选择、
    /// 在运行前拒绝启动，避免把残缺的目录型 configPath 整目录采用为快照。
    /// </summary>
    public IReadOnlyList<string> ConfigInputCandidates { get; set; } = Array.Empty<string>();

    /// <summary>当前 profile 使用的配置输入值。</summary>
    public string ConfigInputValue { get; set; } = "";

    /// <summary>可参与编辑隔离的实际候选路径。</summary>
    public IReadOnlyList<string> ConfigInputCandidatePaths { get; set; } = Array.Empty<string>();

    /// <summary>配置编辑事务声明；缺失时沿用宿主通用编辑语义。</summary>
    public ConfigEditOptions? ConfigEdit { get; set; }

    /// <summary>专项配置编辑脚本；脚本由宿主在准备工作副本后执行。</summary>
    public ConfigEditorDescriptor? ConfigEditor { get; set; }
}
