using System.Diagnostics;
using System.Security.Cryptography;
using NexusPipeline.Modules.Configuration.Contracts;
using NexusPipeline.Modules.Configuration.Exchange;
using NexusPipeline.Modules.Configuration.Paths;
using NexusPipeline.Modules.Configuration.Recovery;
using NexusPipeline.Modules.Configuration.Scripting;
using NexusPipeline.Modules.Configuration.Validation;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Plugins.DataSpecialized;
using NexusPipeline.Modules.Scripts.Contracts;
using NexusPipeline.Modules.Scripts.Resolution;
using NexusPipeline.Modules.Scripts.Validation;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Users.Contracts;
using NexusPipeline.Modules.Users.UseCases;
using NexusPipeline.Modules.Users;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Windows;
using NexusPipeline.Shared.Logging;
using NexusPipeline.Shared.Results;
using NexusPipeline.Modules.Configuration.Snapshots;
using NexusPipeline.Modules.Settings.Contracts;
using NexusPipeline.Modules.Settings;

namespace NexusPipeline.Modules.Configuration.Editing;

/// <summary>配置编辑生命周期的应用命令结果。</summary>
internal sealed record ConfigEditStarted(int ProcessId, string EditMode);

/// <summary>完成配置编辑后的提交结果；validator feedback 随同本次 API 响应返回。</summary>
internal sealed record ConfigEditCompleted(bool Success, ConfigValidationResult Validation);

/// <summary>
/// 配置编辑生命周期应用命令。
/// Web、CLI 和交互菜单只负责协议适配；配置交换、进程控制、租约与会话门禁统一在常驻服务内执行。
/// </summary>
internal sealed partial class ConfigEditCommands
{
    private readonly IConfigEditAdmission _admission;
    private readonly IScriptRepository _scripts;
    private readonly IUserRepository _users;
    private readonly IPluginAvailability _pluginAvailability;
    private readonly IPluginCapabilityResolver _capabilities;
    private readonly ScriptSpecResolver _specResolver;
    private readonly UserCommands _userCommands;
    private readonly ITaskProtocolConfigAssessmentPort? _taskProtocolAssessment;
    private readonly ISettingsProvider? _settingsProvider;

    internal ConfigEditCommands(
        IConfigEditAdmission admission,
        IScriptRepository scripts,
        IUserRepository users,
        IPluginAvailability pluginAvailability,
        IPluginCapabilityResolver capabilities,
        ScriptSpecResolver specResolver,
        UserCommands userCommands,
        ITaskProtocolConfigAssessmentPort? taskProtocolAssessment = null,
        ISettingsProvider? settingsProvider = null)
    {
        _admission = admission;
        _scripts = scripts;
        _users = users;
        _pluginAvailability = pluginAvailability;
        _capabilities = capabilities;
        _specResolver = specResolver;
        _userCommands = userCommands;
        _taskProtocolAssessment = taskProtocolAssessment;
        _settingsProvider = settingsProvider;
    }

    private OperationResult<ConfigEditTarget> ResolveTarget(
        string scriptId,
        string userReference,
        IReadOnlyDictionary<string, string>? inputOverrides = null)
    {
        ScriptInstance? declaration = _scripts.FindById(scriptId);
        if (declaration is null)
        {
            return NotFound<ConfigEditTarget>($"未找到脚本实例：{scriptId}");
        }
        ResolvedScriptUser? binding = _users
            .ResolveBinding(declaration, userReference);
        if (binding is null)
        {
            return NotFound<ConfigEditTarget>("用户绑定不存在");
        }
        // 专项快照按用户绑定输入实例化：接管哪个配置文件/实例目录是用户级选择
        ResolvedScriptSpec spec = _specResolver.Resolve(
            declaration,
            inputOverrides ?? binding.Binding.ConfigInputs, binding.UserId, binding.Binding.ExecutionProviderConfigRevision);
        if (!spec.Succeeded)
        {
            return Validation<ConfigEditTarget>(spec.Error ?? "脚本有效配置解析失败");
        }
        ScriptInstance script = spec.Script;

        return OperationResult<ConfigEditTarget>.Ok(
            new ConfigEditTarget(script, binding, binding.UserId, spec));
    }

    private static string ResolveLaunchTargetExe(ScriptInstance script)
    {
        string workingDir = string.IsNullOrWhiteSpace(script.RootPath)
            ? Path.GetDirectoryName(script.MainExe) ?? ""
            : script.RootPath;
        return SystemActions.ResolveLaunchTarget(script.MainExe, workingDir, script.Args).ExePath;
    }

    private static bool TryCreatePendingInput(
        IReadOnlyDictionary<string, string> inputOverrides,
        out ConfigEditPendingInput? pendingInput,
        out string? error)
    {
        pendingInput = null;
        error = null;
        if (inputOverrides.Count != 1)
        {
            error = "一次只能覆盖一个配置输入";
            return false;
        }

        KeyValuePair<string, string> item = inputOverrides.Single();
        string name = item.Key.Trim();
        string value = item.Value;
        if (string.IsNullOrWhiteSpace(name)
            || string.IsNullOrWhiteSpace(value)
            || name.Length > 128
            || value.Length > 512
            || !char.IsLetter(name[0])
            || name.Any(character => !char.IsLetterOrDigit(character) && character != '_')
            || value.Any(char.IsControl))
        {
            error = "配置输入值格式无效";
            return false;
        }

        pendingInput = new ConfigEditPendingInput { Name = name, Value = value };
        return true;
    }

    private static bool IsSessionUser(ResolvedScriptUser user, string userReference)
    {
        return !string.IsNullOrWhiteSpace(userReference)
            && (string.Equals(user.UserId, userReference, StringComparison.OrdinalIgnoreCase)
                || string.Equals(user.UserName, userReference, StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveSessionUserKey(EditSession session)
    {
        if (!string.IsNullOrWhiteSpace(session.Mark.UserId))
        {
            return session.Mark.UserId;
        }
        if (!string.IsNullOrWhiteSpace(session.User.UserId))
        {
            return session.User.UserId;
        }
        return "";
    }

    /// <summary>归一化编辑方式：空值视为 normal；非法值返回 null。</summary>
    private static string? NormalizeEditMode(string? mode)
    {
        string value = (mode ?? "").Trim().ToLowerInvariant();
        return value switch
        {
            "" or "normal" => "normal",
            "fresh" => "fresh",
            "reuse" => "reuse",
            _ => null,
        };
    }

    /// <summary>config 位置是否存在可入库的配置内容（文件存在，或目录非空）。</summary>
    private static bool ConfigLocationHasContent(string configPath)
    {
        PathKind kind = PathKindUtil.KindOf(configPath);
        if (kind == PathKind.Missing)
        {
            return false;
        }
        if (kind == PathKind.File)
        {
            return true;
        }
        return Directory.Exists(configPath) && Directory.EnumerateFileSystemEntries(configPath).Any();
    }

    private static OperationResult<T> Validation<T>(
        string message,
        string code = "validation_error") =>
        OperationResult<T>.Failure(code, message, OperationErrorKind.Validation);

    private static OperationResult<T> NotFound<T>(string message) =>
        OperationResult<T>.Failure("not_found", message, OperationErrorKind.NotFound);

    private static OperationResult<T> Conflict<T>(string code, string message) =>
        OperationResult<T>.Failure(code, message, OperationErrorKind.Conflict);

    private static OperationResult<T> LeaseConflict<T>(
        IReadOnlyList<ExecutionLeaseReference> leases,
        string resource,
        string? failureCode = null)
    {
        return failureCode == "host_maintenance"
            ? OperationResult<T>.Failure(
                "host_maintenance",
                "宿主正在进行维护操作，暂不能修改运行配置",
                OperationErrorKind.Conflict)
            : OperationResult<T>.Failure(
                "execution_resource_in_use",
                $"执行计划正在引用资源「{resource}」，当前无法修改；请等待相关运行结束",
                OperationErrorKind.Conflict,
                leases.Select(lease => lease.RunId).Distinct(StringComparer.Ordinal).ToArray());
    }

    private static OperationResult<T> Internal<T>(Exception exception) =>
        OperationResult<T>.Failure(
            "internal_error",
            exception.Message,
            OperationErrorKind.Internal);

    private sealed record ConfigEditTarget(
        ScriptInstance Script,
        ResolvedScriptUser User,
        string UserKey,
        ResolvedScriptSpec? Spec = null);
}
