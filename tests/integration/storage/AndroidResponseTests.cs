using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class AndroidResponseTests
{
    [Theory(DisplayName = "Saved Actual Responses Keep Strict Object Semantics Without Normalizing Their Original Text")]
    [InlineData(" \t\r\n{\"code\":1}", (int)ExecutionAndroidState.Failed)]
    [InlineData("Success", (int)ExecutionAndroidState.ApplicationStartRequested)]
    [InlineData("\"Success\"", (int)ExecutionAndroidState.ApplicationStartRequested)]
    [InlineData("\u00a0{\"code\":1}", (int)ExecutionAndroidState.ApplicationStartRequested)]
    [InlineData("{\"code\":0,\"code\":1}", (int)ExecutionAndroidState.ApplicationStartRequested)]
    public async Task PreservesStrictResponses(
        string response, int expected)
    {
        var fixture = new AndroidRuntimeFixture(forceClose: false, responseAction: "start",
            stdout: response, exitCode: 0, waitSeconds: 1);
        await fixture.ExecuteAsync(async () =>
        {
            await fixture.RunAsync();
            var state = fixture.State;
            var reader = new AndroidControlEvidence(fixture.Source.Instance, fixture.Source.Database, new());
            var commands = AndroidControlCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256);
            var command = Assert.Single(commands, item => item.Action == ExecutionAndroidAction.StartApplication);
            var result = Assert.Single(fixture.Collected().Commands, item => item.Binding == command.Binding);
            var collected = reader.Collection(state, command, result.Collection, true);
            var native = reader.Observation(state, command, collected.Observation, true);
            Assert.Equal(AndroidResponseCompletion.NaturalRootExitAndEofBeforeStop, native.Completion);
            Assert.Equal(0, native.ExitCode);
            Assert.Equal(response, reader.ReadText(native).StandardOutput);
            var interpreted = ExecutionAndroidObservations.ReadSaved(reader, reader.Request(state, command), native, collected.Observation);
            Assert.Equal((ExecutionAndroidState)expected, interpreted.State);
            Assert.Equal(TargetCommandObservation.Complete, interpreted.Interpretation);
            Assert.All(collected.Inventory.AllFamily, member =>
                Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
            var main = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total));
            Assert.Equal(EngineOutcome.NotStarted, main.Latest.Engine);
            Assert.Equal(BusinessOutcome.Unverified, main.Latest.Business);
        });
    }
}
