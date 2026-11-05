using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class TargetResourceKeys
{
    internal const string AndroidControl = "target:android:control";

    internal static ResourceClaim Pc(ExecutionPlanInterpreterImage image)
    {
        var identity = image.Identity;
        if (identity is null || identity.VolumeSerial.Length != 16 || identity.FileId.Length != 32 ||
            !identity.VolumeSerial.All(Uri.IsHexDigit) || !identity.FileId.All(Uri.IsHexDigit))
            throw new IOException("execution_target_image_identity_invalid");
        return new("target:pc:object:" + identity.VolumeSerial.ToLowerInvariant() + ":" + identity.FileId.ToLowerInvariant(),
            ResourceClaimMode.Mutation);
    }

    internal static IReadOnlyList<ResourceClaim> Android(string endpoint, ResourceClaimMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        var normalized = ExecutionAndroidCommands.Endpoint(endpoint);
        return [new(AndroidControl, mode), new("target:android:endpoint:" + normalized, mode)];
    }
}
