using System.Runtime.Versioning;
using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class PreparedCaptureContext
{
    private readonly AndroidControlStore _owner;
    private readonly AndroidCaptureFacts _facts;
    private readonly ExecutionPlanInterpreterImage _tool;
    private readonly ExecutionPlanInterpreterImage? _broker, _screenshotTool;
    internal StepEvidenceBinding Primary { get; }

    internal PreparedCaptureContext(AndroidControlStore owner, StepEvidenceBinding primary,
        AndroidCaptureFacts facts, ExecutionPlanInterpreterImage tool,
        ExecutionPlanInterpreterImage? broker = null, ExecutionPlanInterpreterImage? screenshotTool = null)
    {
        _owner = owner; Primary = primary; _facts = facts;
        _tool = Copy(tool); _broker = broker is null ? null : Copy(broker);
        _screenshotTool = screenshotTool is null ? null : Copy(screenshotTool);
        if ((_facts.MuMu is null) != (_broker is null) || _facts.MuMu is null && _screenshotTool is not null)
            throw new IOException("execution_capture_android_unbound");
    }

    internal (AndroidCaptureFacts Facts, ExecutionPlanInterpreterImage Tool) ReadFrozen() =>
        Use(facts => (facts, Copy(_tool)));

    internal (AndroidCaptureFacts Facts, ExecutionPlanInterpreterImage Broker, ExecutionPlanInterpreterImage Manager,
        ExecutionPlanInterpreterImage? Tool) ReadMuMuFrozen() => Use(facts =>
        facts.MuMu is null || _broker is null ? throw new IOException("execution_capture_android_unbound") :
            (facts, Copy(_broker), Copy(_tool), _screenshotTool is null ? null : Copy(_screenshotTool)));

    internal T Use<T>(Func<AndroidCaptureFacts, T> action) => _owner.UseCapture(this, () => action(_facts));
    internal ValueTask RequireCurrentAsync(CancellationToken cancellation) => _owner.RequireTargetAsync(cancellation);

    private static ExecutionPlanInterpreterImage Copy(ExecutionPlanInterpreterImage value) =>
        System.Text.Json.JsonSerializer.Deserialize<ExecutionPlanInterpreterImage>(
            NexusPipeline.Platform.Serialization.BoundedJson.Serialize(value, StepResultCodec.Options),
            StepResultCodec.Options) ?? throw new IOException("execution_capture_android_unbound");
}
