using System.Runtime.Versioning;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class AndroidDetachmentTests
{
    [Fact(DisplayName = "Deriving A Request Detaches Its Only Mutable Collection Without Creating Any Command Or Effect")]
    public void DetachesRequestCollections()
    {
        using var fixture = new AndroidRuntimeFixture(launch: false, forceClose: false);
        var state = fixture.State; var step = Assert.Single(state.Total.Steps);
        var sourceFiles = Assert.IsAssignableFrom<IList<ExecutionPlanRuntimeFile>>(step.Target!.Android!.Tool.RuntimeFiles);
        Assert.NotEmpty(sourceFiles);
        var binding = new ExecutionTargetBinding(fixture.Source.Instance.InstanceId, state.Journal.JournalId,
            state.Journal.OperationId, Guid.Parse(state.Attempt.AttemptId), step.StepId, Guid.NewGuid(),
            "Local\\NexusWorker-" + Guid.NewGuid().ToString("N"), Guid.NewGuid(), 0, state.Attempt.ExecutionPlan.Sha256);
        var request = AndroidCommandIdentity.Derive(state.Total, binding, AndroidRuntimeFixture.Subject,
            0, AndroidCommandPurpose.CleanupReadiness, ExecutionAndroidAction.DeviceState);
        var original = request.Tool.RuntimeFiles.ToArray(); var digest = AndroidCommandIdentity.Digest(request);
        Assert.Throws<NotSupportedException>(() => ((IList<ExecutionPlanRuntimeFile>)request.Tool.RuntimeFiles).Clear());
        sourceFiles.Clear();
        Assert.Equal(original, request.Tool.RuntimeFiles);
        Assert.Equal(digest, AndroidCommandIdentity.Digest(request));
        Assert.Equal(AndroidRuntimeFixture.Subject, request.SubjectId);
        Assert.Equal(0, fixture.Base.Count("execution_target_command_jobs"));
        Assert.Equal(0, fixture.Base.Count("execution_target_job_roots"));
        Assert.False(Directory.Exists(Path.Combine(fixture.Directory, "observed")));
        fixture.VerifyOriginal();
    }
}
