namespace NexusPipeline.Plugin.Abstractions;

public interface IPluginDashboardCardRegistry
{
    IDisposable Register(PluginDashboardCardDescriptor descriptor);
}

public sealed record PluginDashboardCardDescriptor(
    string Id, string TitleKey, string DescriptionKey, bool DefaultVisible, int DefaultOrder);
