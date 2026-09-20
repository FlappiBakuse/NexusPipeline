using NexusPipeline.Modules.Configuration.Editing;
using NexusPipeline.Modules.Scripts.Contracts;
using NexusPipeline.Modules.Users.Contracts;
using NexusPipeline.Modules.Users;

namespace NexusPipeline.Modules.Users.UseCases;

/// <summary>用户命令 facade；具体领域操作按 profile、global settings、binding 与 avatar 分文件维护。</summary>
internal sealed partial class UserCommands
{
    private readonly IUserMutationState _state;
    private readonly IUserMutationAdmission _admission;
    private readonly IUserMutationPolicy _policy;
    private readonly IUserPlansChanged _plansChanged;
    private readonly IScriptRepository _scripts;
    private readonly IScriptConfigGate _scriptConfigGate;
    private readonly IUserConfigDataMaintenance _configData;
    private readonly UserAssetService _assets;
    private readonly IUserDeletionTransaction _deletion;

    internal UserCommands(
        IUserMutationState state,
        IUserMutationAdmission admission,
        IUserMutationPolicy policy,
        IUserPlansChanged plansChanged,
        IScriptRepository scripts,
        IScriptConfigGate scriptConfigGate,
        IUserConfigDataMaintenance configData,
        UserAssetService assets,
        IUserDeletionTransaction deletion)
    {
        _state = state;
        _admission = admission;
        _policy = policy;
        _plansChanged = plansChanged;
        _scripts = scripts;
        _scriptConfigGate = scriptConfigGate;
        _configData = configData;
        _assets = assets;
        _deletion = deletion;
    }
}
