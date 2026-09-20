using NexusPipeline.Modules.Plugins.Contracts;

namespace NexusPipeline.Tests.Support;

internal sealed class AllowAllPluginAvailability : IPluginAvailability
{
    public bool IsKnownPlugin(string pluginName) => true;

    public bool IsDataSpecializedPlugin(string pluginName) => true;

    public bool IsEnabled(string pluginName) => true;
}
