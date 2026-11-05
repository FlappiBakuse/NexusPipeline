using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Core.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class ParticipantTextTests
{
    [Theory(DisplayName = "Previously Decoded Participant Text Never Authorizes Changed Metadata Or Outer Kind")]
    [InlineData("version")]
    [InlineData("kind")]
    [InlineData("extra")]
    [InlineData("omitted")]
    public void RechecksOuterMetadata(string fault)
    {
        var participant = WriterIntentCodec.Participant(Sample(), "fixture-script");
        Assert.True(WriterIntentCodec.IsParticipant(JsonSerializer.SerializeToElement(participant)));
        var identity = participant["identity"]!.AsObject();
        if (fault == "version") identity["version"] = 2;
        else if (fault == "kind") participant["kind"] = "directory";
        else if (fault == "extra") identity["unrecognized"] = 1;
        else Assert.True(identity.Remove("fileLogBaseline"));
        Assert.Throws<JsonException>(() => WriterIntentCodec.IsParticipant(JsonSerializer.SerializeToElement(participant)));
    }

    [Fact(DisplayName = "Decoded Participant Records Stay Independent Of Caller Trees And Other Clr Types")]
    public void OwnsDecodedRecords()
    {
        var original = Sample(); var node = JsonSerializer.SerializeToNode(original, StepResultCodec.Options)!.AsObject();
        var first = ExecutionParticipantText.Read<StepWriterIntent>(JsonSerializer.SerializeToElement(node),
            StepResultCodec.Options, Invalid);
        var changed = Guid.NewGuid(); node["writerId"] = changed.ToString("D");
        var next = ExecutionParticipantText.Read<StepWriterIntent>(JsonSerializer.SerializeToElement(node),
            StepResultCodec.Options, Invalid);
        Assert.True(first.MatchesOriginal); Assert.True(next.MatchesOriginal);
        Assert.Equal(original.WriterId, first.Value.WriterId); Assert.Equal(changed, next.Value.WriterId);
        Assert.Throws<JsonException>(() => ExecutionParticipantText.Read<ExecutionCaptureIntent>(
            JsonSerializer.SerializeToElement(node), StepResultCodec.Options, Invalid));
    }

    [Fact(DisplayName = "Concurrent Distinct Participant Texts Keep Their Own Identities And Repeat Outer Validation")]
    public void IsolatesConcurrentTexts()
    {
        Parallel.For(0, 600, index =>
        {
            var original = Sample() with { StepId = "step-" + index };
            var node = WriterIntentCodec.Participant(original, "fixture-script");
            Assert.True(WriterIntentCodec.IsParticipant(JsonSerializer.SerializeToElement(node)));
            node["kind"] = "directory";
            Assert.Throws<JsonException>(() => WriterIntentCodec.IsParticipant(JsonSerializer.SerializeToElement(node)));
            var parsed = ExecutionParticipantText.Read<StepWriterIntent>(
                JsonSerializer.SerializeToElement(node["identity"]), StepResultCodec.Options, Invalid);
            Assert.Equal(original, parsed.Value); Assert.True(parsed.MatchesOriginal);
        });
    }

    private static StepWriterIntent Sample() => new(WriterIntentCodec.Format, 1, "fixture-step", "registered",
        "Local\\NexusWorker-" + Guid.NewGuid().ToString("N"), Guid.NewGuid(), 2, null);
    private static JsonException Invalid() => new("fixture_participant_invalid");

    [Theory(DisplayName = "Decoded PC Stage Text Still Rechecks Changed Metadata And Its Outer Kind")]
    [InlineData("version")]
    [InlineData("kind")]
    [InlineData("extra")]
    [InlineData("omitted")]
    public void RechecksPcMetadata(string fault)
    {
        var digest = new string('a', 64);
        var binding = new PcStageBinding(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "step", digest);
        var value = new PcStageIntent(PcStageCodec.Format, 1, binding, TargetExecutionOutcome.Skipped,
            PcStageCodec.Reason(TargetExecutionOutcome.Skipped), new BlobReference($"blobs/plans/{digest}.json", 1, digest));
        var participant = PcStageCodec.Participant(value, "script");
        Assert.True(PcStageCodec.IsParticipant(JsonSerializer.SerializeToElement(participant)));
        var identity = participant["identity"]!.AsObject();
        if (fault == "version") identity["version"] = 2;
        else if (fault == "kind") participant["kind"] = "directory";
        else if (fault == "extra") identity["unrecognized"] = 1;
        else Assert.True(identity.Remove("reasonCode"));
        Assert.ThrowsAny<Exception>(() => PcStageCodec.IsParticipant(JsonSerializer.SerializeToElement(participant)));
    }
}
