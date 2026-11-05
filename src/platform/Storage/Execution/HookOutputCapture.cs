using System.Runtime.Versioning;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class HookOutputCapture : IDisposable
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly ContractReader _contracts;
    private readonly InstanceLease _lease;
    private readonly StepEvidenceBinding _binding;
    private readonly string _role;
    private readonly ConfigurationWriterProcess? _writer;
    private readonly ConfigurationRunController? _controller;
    private readonly AuxiliaryOutputCapture _capture;

    internal HookOutputCapture(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease,
        StepEvidenceBinding binding, string role, ConfigurationWriterProcess? writer, ConfigurationRunController? controller,
        BindingRawAllowance? bindingAllowance = null, AttemptWriterState? preparedState = null)
    {
        if (!ExecutionBindingRoles.IsHook(role) || (writer is null) == (controller is null)) throw new IOException("execution_hook_capture_unbound");
        _instance = instance; _database = database; _contracts = contracts; _lease = lease;
        _binding = binding; _role = role; _writer = writer; _controller = controller;
        var reader = new StepEvidenceReader(instance, database, contracts);
        _ = preparedState is null ? reader.Context(binding, role) : reader.ContextFromValidatedRead(binding,
            preparedState.Journal, preparedState.Attempt, preparedState.Total, preparedState.Latest, role);
        _capture = new(new(instance, contracts), binding, bindingAllowance is null ? BindingOutputBudget.Remaining(instance, database, contracts, binding) : RawOutputCodec.MaximumRecordedBytes,
            "execution_hook_start_closed", "execution_hook_output_collection_pending", bindingAllowance,
            lease.LiveLogs.Register(instance, database, contracts, binding, role));
    }

    internal void Start() => _capture.Start(output =>
    { if (_writer is not null) _writer.Start(output); else _controller!.Start(output); });

    internal void ObserveRootExit()
    {
        if (_writer is not null) _writer.UseStepEvidence(_binding, null, (_, _) => true);
        else _controller!.UseStepEvidence(_binding, null, (_, _) => true);
    }

    internal async Task<PreparedHookResult> CollectAsync(StoppedConfigurationEditor stopped, TimeSpan timeout)
    {
        var raw = await _capture.CollectAsync(timeout).ConfigureAwait(false);
        return _writer is not null
            ? PreparedHookResult.Live(_instance, _database, _contracts, _lease, _binding, _role, raw, _writer, stopped)
            : PreparedHookResult.Live(_instance, _database, _contracts, _lease, _binding, _role, raw, _controller!, stopped);
    }

    public void Dispose() => _capture.Dispose();
}
