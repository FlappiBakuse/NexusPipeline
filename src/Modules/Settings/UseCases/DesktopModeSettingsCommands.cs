using NexusPipeline.Modules.Settings.Contracts;
using NexusPipeline.Modules.Settings.Persistence;
using NexusPipeline.Shared.Results;

namespace NexusPipeline.Modules.Settings.UseCases;

internal sealed class DesktopModeSettingsCommands(SettingsState state, ISettingsMutationGate mutationGate)
{
    private readonly SettingsState _state = state;
    private readonly ISettingsMutationGate _mutationGate = mutationGate;
    internal bool SavedLightweightMode => _state.Current.LightweightMode;

    internal OperationResult<bool> SetLightweightMode(bool expected, bool value)
    {
        bool conflict = false;
        try
        {
            if (!_mutationGate.TryExecute(() =>
            {
                lock (_state.MutationLock)
                {
                    AppSettings current = _state.Current;
                    if (current.LightweightMode != expected) { conflict = true; return; }
                    AppSettings candidate = current.Clone();
                    candidate.LightweightMode = value;
                    AppSettingsStore.Save(candidate);
                    _state.ReplaceAfterSave(candidate);
                }
            }, out string? code))
                return OperationResult<bool>.Failure(code ?? "host_maintenance", "宿主正在维护，请稍后重试", OperationErrorKind.Conflict);
            return conflict
                ? OperationResult<bool>.Failure("desktop_mode_changed", "轻量模式已改变，请重试", OperationErrorKind.Conflict)
                : OperationResult<bool>.Ok(value);
        }
        catch (Exception) { return OperationResult<bool>.Failure("settings_save_failed", "轻量模式保存失败", OperationErrorKind.Internal); }
    }

}
