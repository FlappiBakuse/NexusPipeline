namespace NexusPipeline.Modules.Settings.Contracts;

/// <summary>Host admission port for settings mutations.</summary>
internal interface ISettingsMutationGate
{
    bool TryExecute(Action mutation, out string? failureCode);
}
