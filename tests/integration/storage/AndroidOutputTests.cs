using System.Runtime.Versioning;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class AndroidOutputTests
{
    [Fact(DisplayName = "One Actual Binding Shares Its Twenty Mi B Allowance Across Hooks Main File Logs And Android Commands")]
    public async Task SharesBindingAllowance()
    {
        var fixture = new AndroidRuntimeFixture(forceClose: false, hooks: true, sharedOutput: true);
        await fixture.ExecuteAsync(async () =>
        {
            await fixture.RunAsync();
            var state = fixture.State;
            var step = Assert.Single(state.Total.Steps);
            var blobs = new ImmutableBlobStore(fixture.Source.Instance, new());
            var hookReader = new HookEvidenceReader(fixture.Source.Instance, fixture.Source.Database, new());
            var hooks = HookResultCodec.Read(state.Latest.Root, state.Total);
            Assert.Equal(2, hooks.Length);
            var outputs = hooks.ToDictionary(hook => hook.Role, hook =>
                RawOutputCodec.Read(blobs, hook.Binding, hookReader.Read(hook.Binding, hook.Role, hook.Artifact, true).RawOutput!));
            var primary = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total));
            var primaryEvidence = StepEvidenceCodec.Read(blobs, primary.Binding, primary.Latest.Artifact);
            var main = RawOutputCodec.Read(blobs, primary.Binding, primaryEvidence.RawOutput!);
            Assert.True(main.IsComplete);
            Assert.True(outputs["preHook"].IsComplete);
            Assert.Equal(0, main.DiscardedBytes);
            Assert.Equal(0, outputs["preHook"].DiscardedBytes);
            Assert.True(outputs["preHook"].Parts.Sum(part => part.Content.SizeBytes) >= 8388608);
            Assert.True(main.Parts.Where(part => part.FileSourceId is null).Sum(part => part.Content.SizeBytes) >= 6291456);
            Assert.True(main.Parts.Where(part => part.FileSourceId is not null).Sum(part => part.Content.SizeBytes) >= 2097152);
            Assert.False(outputs["postHook"].IsComplete);
            Assert.True(outputs["postHook"].DiscardedBytes > 0);
            Assert.True(outputs["postHook"].ObservedBytes >= 8388608);
            foreach (var raw in outputs.Values.Append(main))
            {
                Assert.Equal(raw.ObservedBytes, raw.Parts.Sum(part => part.Content.SizeBytes) + raw.DiscardedBytes);
                foreach (var part in raw.Parts) blobs.Verify(part.Content);
            }
            var commands = AndroidControlCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256);
            var commandReader = new AndroidControlEvidence(fixture.Source.Instance, fixture.Source.Database, new());
            var result = fixture.Collected();
            long toolBytes = 0;
            foreach (var command in commands)
            {
                var actual = Assert.Single(result.Commands, item => item.Binding == command.Binding);
                var collected = commandReader.Collection(state, command, actual.Collection, true);
                var native = commandReader.Observation(state, command, collected.Observation, true);
                Assert.True(native.OutputComplete);
                Assert.Equal(AndroidResponseCompletion.NaturalRootExitAndEofBeforeStop, native.Completion);
                Assert.True(native.Parts.Sum(part => part.Content.SizeBytes) >= 262144);
                foreach (var part in native.Parts) blobs.Verify(part.Content);
                toolBytes += native.Parts.Sum(part => part.Content.SizeBytes);
            }
            Assert.NotEmpty(commands);
            var total = toolBytes + main.Parts.Sum(part => part.Content.SizeBytes) +
                outputs.Values.Sum(raw => raw.Parts.Sum(part => part.Content.SizeBytes));
            Assert.InRange(total, 1, 20L * 1024 * 1024);
            Assert.Equal(total, BindingOutputBudget.Used(fixture.Source.Instance, fixture.Source.Database, new(),
                fixture.Accepted.JournalId, step.StepId));
            foreach (var raw in outputs.Values.Append(main))
                foreach (var part in raw.Parts)
                    using (var content = blobs.OpenVerified(part.Content)) Assert.Equal(part.Content.SizeBytes, content.Length);
        });
    }
}
