using NexusPipeline.Plugin.Abstractions;

namespace NexusPipeline.Modules.Plugins.Contracts;

internal sealed record ExecutionProviderDescriptor(
    string PluginId, string PluginVersion, string PluginDirectory, IPluginExecutionProvider Provider);

internal interface IPluginExecutionProviderResolver
{
    ExecutionProviderDescriptor? ResolveExecutionProvider(string providerId);
}
