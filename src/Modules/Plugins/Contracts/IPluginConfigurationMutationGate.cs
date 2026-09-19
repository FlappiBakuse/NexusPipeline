namespace NexusPipeline.Modules.Plugins.Contracts;

/// <summary>Host admission port for plugin-managed configuration writes.</summary>
internal interface IPluginConfigurationMutationGate
{
    bool TryExecute(Action mutation, out string? failureCode);
}
