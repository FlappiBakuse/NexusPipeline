using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class OfflineRuleTests
{
    [Theory(DisplayName = "Complete Error Text Cannot Confirm This Device Offline")]
    [InlineData("", "adb: failed to connect to daemon at tcp:5037: Connection refused", 1, TargetCommandObservation.Unverified)]
    [InlineData("ok", "error: device offline", 1, TargetCommandObservation.Contradictory)]
    [InlineData("", "error: device '127.0.0.1:9999' not found", 1, TargetCommandObservation.Unverified)]
    [InlineData("", "error: insufficient permissions for device", 1, TargetCommandObservation.Unverified)]
    public void RejectsOfflineErrors(string stdout, string stderr, int exitCode,
        TargetCommandObservation expected)
    {
        var artifact = new BlobReference("blobs/plans/" + new string('a', 64) + ".json", 1, new string('a', 64));
        var actual = ExecutionAndroidObservations.Interpret(artifact, ExecutionAndroidAction.OfflineProbe,
            ExecutionAndroidDriver.GenericAdb, null, null, "127.0.0.1:16384", exitCode, stdout, stderr);
        Assert.Equal(artifact, actual.Artifact); Assert.Equal(ExecutionAndroidAction.OfflineProbe, actual.Action);
        Assert.Equal(ExecutionAndroidState.Unknown, actual.State); Assert.Equal(expected, actual.Interpretation);
        Assert.Equal(0, ExecutionAndroidObservations.ConsecutiveOffline(1, actual));
    }
}
