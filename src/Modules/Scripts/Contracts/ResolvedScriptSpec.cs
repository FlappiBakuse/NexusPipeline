using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Plugins.DataSpecialized;
using NexusPipeline.Modules.Scripts;
namespace NexusPipeline.Modules.Scripts.Contracts;


/// <summary>
/// 脚本声明解析后的不可变运行时快照。
/// Script 是兼容现有执行域的完整快照；PluginVersion/ProfileHash 用于恢复、调度诊断与后续存储元数据。
/// </summary>
internal sealed record ResolvedScriptSpec(
    ScriptInstance Script,
    string PluginVersion,
    ResolvedJudgeScript JudgeScript,
    string ProfileHash,
    string? Error = null,
    ConfigEditorDescriptor? ConfigEditor = null)
{
    /// <summary>专项插件的附加配置路径（extraConfigPaths）；通用脚本为空。仅参与按用户快照交换与校验器只读。</summary>
    public IReadOnlyList<string> ExtraConfigPaths { get; init; } = Array.Empty<string>();

    /// <summary>configPath 模板的绑定输入处于未定状态时的候选清单：编辑启动要求用户选择、运行前拒绝启动。</summary>
    public IReadOnlyList<string> ConfigInputCandidates { get; init; } = Array.Empty<string>();

    /// <summary>候选清单对应的真实插件输入名。</summary>
    public string ConfigInputName { get; init; } = "";

    /// <summary>当前 profile 实际使用的配置输入值。</summary>
    public string ConfigInputValue { get; init; } = "";

    /// <summary>配置编辑事务声明。</summary>
    public ConfigEditOptions? ConfigEdit { get; init; }

    /// <summary>configPath 模板展开后的实际候选路径。</summary>
    public IReadOnlyList<string> ConfigInputCandidatePaths { get; init; } = Array.Empty<string>();

    /// <summary>插件声明 PC 启动由自身管理；仅在 PC 运行期屏蔽宿主启动，不修改持久脚本。</summary>
    public bool SelfManagedPcLaunch { get; init; }

    public TaskProtocolDescriptor? TaskProtocol { get; init; }

    public bool Succeeded => string.IsNullOrWhiteSpace(Error);
}
