using System.Runtime.Versioning;
using NexusPipeline.Platform.Storage.Configuration;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class PreparedStepObservation
{
    private readonly StepEvidenceAuthority _authority;
    internal StepEvidenceBinding Binding => _authority.Binding;
    internal PreparedStepObservation(StepEvidenceAuthority authority) => _authority = authority;
    internal T Use<T>(StepEvidenceBinding expected, Func<ExecutionStepEvidence, T> callback) => _authority.Use(expected, callback);
}

[SupportedOSPlatform("windows")]
internal sealed class PreparedStepResult
{
    private readonly StepEvidenceAuthority _authority;
    internal StepEvidenceBinding Binding => _authority.Binding;
    internal PreparedStepResult(StepEvidenceAuthority authority) => _authority = authority;
    internal T Use<T>(StepEvidenceBinding expected, Func<ExecutionStepEvidence, T> callback) => _authority.Use(expected, callback);
}

[SupportedOSPlatform("windows")]
internal sealed class StepEvidenceAuthority
{
    private readonly StepEvidenceFactory _factory;
    private readonly ExecutionStepEvidence _evidence;
    private readonly ConfigurationWriterProcess? _writer;
    private readonly ConfigurationRunController? _controller;
    private readonly StoppedConfigurationEditor? _stopped;
    internal StepEvidenceBinding Binding { get; }
    private StepEvidenceAuthority(StepEvidenceFactory factory, StepEvidenceBinding binding,
        ExecutionStepEvidence evidence, ConfigurationWriterProcess? writer, ConfigurationRunController? controller, StoppedConfigurationEditor? stopped)
    { _factory = factory; Binding = binding; _evidence = evidence; _writer = writer; _controller = controller; _stopped = stopped; }

    internal static StepEvidenceAuthority Live(StepEvidenceFactory factory, StepEvidenceBinding binding,
        ExecutionStepEvidence evidence, ConfigurationWriterProcess writer, StoppedConfigurationEditor? stopped) => new(factory, binding, evidence, writer, null, stopped);
    internal static StepEvidenceAuthority Live(StepEvidenceFactory factory, StepEvidenceBinding binding,
        ExecutionStepEvidence evidence, ConfigurationRunController controller, StoppedConfigurationEditor? stopped) => new(factory, binding, evidence, null, controller, stopped);
    internal static StepEvidenceAuthority Recovery(StepEvidenceFactory factory, StepEvidenceBinding binding,
        ExecutionStepEvidence evidence) => new(factory, binding, evidence, null, null, null);

    internal T Use<T>(StepEvidenceBinding expected, Func<ExecutionStepEvidence, T> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (expected != Binding) throw new IOException("execution_step_evidence_owner_unbound");
        if (_writer is not null) return _writer.UseStepEvidence(Binding, _stopped,
            (root, code) => _factory.UseLive(Binding, _evidence, _stopped is not null, root, code, callback));
        if (_controller is not null) return _controller.UseStepEvidence(Binding, _stopped,
            (root, code) => _factory.UseLive(Binding, _evidence, _stopped is not null, root, code, callback));
        return _factory.UseRecovery(Binding, _evidence, callback);
    }
}
