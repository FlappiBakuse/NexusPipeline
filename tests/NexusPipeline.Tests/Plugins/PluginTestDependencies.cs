using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Settings;
using NexusPipeline.Modules.Settings.Contracts;
using NexusPipeline.Plugin.Abstractions;

namespace NexusPipeline.Tests.Plugins;

internal sealed class TestSettingsProvider : ISettingsProvider
{
    public TestSettingsProvider(AppSettings settings)
    {
        Current = settings;
    }

    public AppSettings Current { get; }
}

internal sealed class TestPluginNotificationSink : IPluginNotificationSink
{
    public ValueTask SendAsync(PluginNotification notification, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
}

internal sealed class AllowAllPluginConfigurationMutationGate : IPluginConfigurationMutationGate
{
    public bool TryExecute(Action mutation, out string? failureCode)
    {
        mutation();
        failureCode = null;
        return true;
    }
}
