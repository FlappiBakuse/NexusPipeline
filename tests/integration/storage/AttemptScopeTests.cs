using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.Versioning;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class AttemptScopeTests
{
    [Fact(DisplayName = "Attempt Parsing Reuses Its Original View And Owned Plan While Rejecting Changed Views And Plans")]
    public async Task RespectsOwnedInputs()
    {
        var fixture = new PcStageFixture(hooks: false);
        await fixture.ExecuteAsync(async () =>
        {
            await fixture.Consumer.RunNextAsync(); var state = fixture.State;
            var root = state.Latest.Root; var total = state.Total;
            using var memo = new AttemptReadMemo(total);
            using var scope = new AttemptReadScope(state.RequireValidatedRead().History, total, memo);
            var jobs = WriterIntentCodec.ReadJobs(root, total);
            Assert.NotEmpty(jobs);
            Assert.NotEmpty(StepResultCodec.Read(root, total));
            Assert.NotEmpty(StepResultCodec.Coverage(root, total));
            var changed = JsonNode.Parse(state.Latest.Export())!.AsObject();
            var participant = changed["participants"]!.AsArray().Single(item => item!["identity"]!["format"]!.GetValue<string>() ==
                WriterIntentCodec.Format && item["identity"]!["stage"]!.GetValue<string>() == "registered");
            participant!["kind"] = "directory";
            Assert.Throws<JsonException>(() => WriterIntentCodec.ReadJobs(JsonSerializer.SerializeToElement(changed), total));
            var wire = JsonSerializer.SerializeToNode(total, StepResultCodec.Options)!;
            wire["steps"]![0]!["scriptId"] = "changed-script";
            var changedPlan = wire.Deserialize<ExecutionPlan>(StepResultCodec.Options)!;
            Assert.Throws<JsonException>(() => WriterIntentCodec.ReadJobs(root, changedPlan));
            var clone = JsonSerializer.Deserialize<ExecutionPlan>(JsonSerializer.Serialize(total, StepResultCodec.Options), StepResultCodec.Options)!;
            using var nested = new AttemptReadScope(state.RequireValidatedRead().History, clone, memo);
            Assert.Equal(jobs.ToArray(), WriterIntentCodec.ReadJobs(root, clone).ToArray());
            Assert.Throws<InvalidOperationException>(() => scope.Dispose());
            nested.Dispose();
            Assert.Equal(jobs.ToArray(), WriterIntentCodec.ReadJobs(root, total).ToArray());
            scope.Dispose();
            Assert.False(AttemptReadScope.TryRead<StepWriterState>(root, total, out _));
            using var following = new AttemptReadScope(state.RequireValidatedRead().History, total, memo);
            Assert.Equal(jobs.ToArray(), WriterIntentCodec.ReadJobs(root, total).ToArray());
            Assert.Throws<JsonException>(() => WriterIntentCodec.ReadJobs(JsonSerializer.SerializeToElement(changed), total));
            Assert.Throws<JsonException>(() => WriterIntentCodec.ReadJobs(root, changedPlan));
            memo.Dispose();
            Assert.Equal(jobs.ToArray(), WriterIntentCodec.ReadJobs(root, total).ToArray());
        });
    }
}
