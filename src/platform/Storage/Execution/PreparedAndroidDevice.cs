using System.Runtime.Versioning;
using NexusPipeline.Platform.Serialization.Protocol;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class PreparedAndroidDevice(AndroidControlStore owner, StepEvidenceBinding primary, ExecutionTargetAndroid target)
{
    internal StepEvidenceBinding Primary { get; } = primary;
    internal ExecutionTargetAndroid Target { get; } = System.Text.Json.JsonSerializer.Deserialize<ExecutionTargetAndroid>(
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(target, StepResultCodec.Options), StepResultCodec.Options)
        ?? throw new IOException("execution_device_target_unbound");
    internal T Use<T>(Func<ExecutionTargetAndroid, T> action) => owner.UseDevice(this, () => action(Target));
    internal ValueTask RequireCurrentAsync(CancellationToken cancellation) => owner.RequireTargetAsync(cancellation);
}
