using NexusPipeline.Modules.Configuration.Paths;
using NexusPipeline.Modules.Plugins;
using NexusPipeline.Modules.Plugins.Runtime;
using NexusPipeline.Modules.Users.Contracts;

namespace NexusPipeline.Host.Composition.Adapters;

/// <summary>Host-owned cleanup for user-owned configuration and plugin data.</summary>
internal sealed class UserConfigDataMaintenance : IUserConfigDataMaintenance
{
    private readonly PluginManager _plugins;

    public UserConfigDataMaintenance(PluginManager plugins)
    {
        _plugins = plugins;
    }

    public void RemoveUserData(string userId)
    {
        _plugins.DeleteUserData(userId);
    }

    public void RemoveUserScriptData(string userId, string scriptId)
    {
        ConfigWorkAreaService.RemoveUserData(scriptId, userId);
        _plugins.DeleteUserScriptData(userId, scriptId);
    }
}
