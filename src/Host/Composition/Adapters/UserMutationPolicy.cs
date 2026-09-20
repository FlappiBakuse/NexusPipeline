using NexusPipeline.Modules.Configuration.Editing;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Scheduling;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Scripts.Validation;
using NexusPipeline.Modules.Users;
using NexusPipeline.Modules.Users.Bindings;
using NexusPipeline.Modules.Users.Contracts;

namespace NexusPipeline.Host.Composition.Adapters;

internal sealed class UserMutationPolicy : IUserMutationPolicy
{
    private readonly ExecutionDispatcher _dispatcher;
    private readonly Scheduler _scheduler;
    private readonly IPluginAvailability _plugins;

    public UserMutationPolicy(
        ExecutionDispatcher dispatcher,
        Scheduler scheduler,
        IPluginAvailability plugins)
    {
        _dispatcher = dispatcher;
        _scheduler = scheduler;
        _plugins = plugins;
    }

    public UserMutationBlock? CheckUser(NexusUser user)
    {
        if (_scheduler.HasPendingUser(user.Id))
        {
            return new UserMutationBlock("resource_busy", "用户已存在待执行的冻结计划，暂时无法修改");
        }
        foreach (UserScriptBinding binding in user.Bindings)
        {
            if (CheckBinding(user.Id, binding.ScriptInstanceId) is UserMutationBlock error)
            {
                return error;
            }
        }
        return null;
    }

    public UserMutationBlock? CheckBinding(string userId, string scriptId)
    {
        if (_dispatcher.FindLeases(scriptId, userId).Count > 0)
        {
            return new UserMutationBlock("resource_busy", "用户绑定正在运行，无法修改");
        }
        if (ConfigEditSessionRegistry.EditSessions.Values.Any(session =>
            session.Script.Id == scriptId
            && string.Equals(session.Mark.UserId, userId, StringComparison.OrdinalIgnoreCase)))
        {
            return new UserMutationBlock("resource_busy", "用户绑定正在编辑配置，无法修改");
        }
        if (_scheduler.HasPendingBinding(userId, scriptId))
        {
            return new UserMutationBlock("resource_busy", "该用户绑定已存在待执行的冻结计划，暂时无法修改");
        }
        return null;
    }

    public UserMutationBlock? CheckScript(string scriptId)
    {
        if (_dispatcher.FindLeases(scriptId).Count > 0)
        {
            return new UserMutationBlock("resource_busy", "脚本正在运行，无法新增绑定");
        }
        if (ConfigEditSessionRegistry.EditSessions.Values.Any(session => session.Script.Id == scriptId))
        {
            return new UserMutationBlock("resource_busy", "脚本正在编辑配置中，无法新增绑定");
        }
        return null;
    }

    public string? CheckScriptPluginAvailability(ScriptInstance script) =>
        PluginAvailability.GetUnavailableReason(script, _plugins);
}
