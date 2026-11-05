using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class AndroidDecodingTests
{
    [Fact(DisplayName = "Decoded Request Reuse Cannot Expose Mutable Caller Files Or Accept Changed Original Bytes")]
    public void OwnsDecodedRequests()
    {
        using var fixture = new AndroidRuntimeFixture(launch: false, forceClose: false);
        var request = Request(fixture);
        var callerFiles = request.Tool.RuntimeFiles.ToList();
        var caller = request with { Tool = request.Tool with { RuntimeFiles = callerFiles } };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(caller, StepResultCodec.Options);
        var decoded = AndroidControlCodec.Decode<AndroidCommandRequest>(bytes);
        var digest = AndroidCommandIdentity.Digest(decoded);
        Assert.NotEmpty(decoded.Tool.RuntimeFiles);
        callerFiles.Clear();
        Assert.Throws<NotSupportedException>(() => ((IList<ExecutionPlanRuntimeFile>)decoded.Tool.RuntimeFiles).Clear());
        var first = bytes[0]; bytes[0] = 0;
        Assert.Throws<JsonException>(() => AndroidControlCodec.Decode<AndroidCommandRequest>(bytes));
        bytes[0] = first;
        Assert.Equal(digest, AndroidCommandIdentity.Digest(AndroidControlCodec.Decode<AndroidCommandRequest>(bytes)));
        Assert.Equal(0, fixture.Base.Count("execution_target_command_jobs")); fixture.VerifyOriginal();
    }

    [Fact(DisplayName = "Concurrent Different Document Bytes Keep Their Own Subjects Across Bounded Decoder Reuse")]
    public void IsolatesConcurrentSubjects()
    {
        using var fixture = new AndroidRuntimeFixture(launch: false, forceClose: false);
        var request = Request(fixture);
        Parallel.For(0, 600, index =>
        {
            var subject = "document-owner-" + index.ToString(CultureInfo.InvariantCulture);
            var wire = request with { SubjectId = subject };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(wire, StepResultCodec.Options);
            Assert.Equal(subject, AndroidControlCodec.Decode<AndroidCommandRequest>(bytes).SubjectId);
            var changed = JsonSerializer.SerializeToUtf8Bytes(wire with { SubjectId = subject + "-changed" }, StepResultCodec.Options);
            Assert.Equal(subject + "-changed", AndroidControlCodec.Decode<AndroidCommandRequest>(changed).SubjectId);
            Assert.Equal(subject, AndroidControlCodec.Decode<AndroidCommandRequest>(bytes).SubjectId);
        });
        Assert.Equal(0, fixture.Base.Count("execution_target_command_jobs")); fixture.VerifyOriginal();
    }

    private static AndroidCommandRequest Request(AndroidRuntimeFixture fixture)
    {
        var state = fixture.State; var step = Assert.Single(state.Total.Steps);
        var binding = new ExecutionTargetBinding(fixture.Source.Instance.InstanceId, state.Journal.JournalId, state.Journal.OperationId,
            Guid.Parse(state.Attempt.AttemptId), step.StepId, Guid.NewGuid(), "Local\\NexusWorker-" + Guid.NewGuid().ToString("N"),
            Guid.NewGuid(), 0, state.Attempt.ExecutionPlan.Sha256);
        return AndroidCommandIdentity.Derive(state.Total, binding, AndroidRuntimeFixture.Subject,
            0, AndroidCommandPurpose.CleanupReadiness, ExecutionAndroidAction.DeviceState);
    }
}
