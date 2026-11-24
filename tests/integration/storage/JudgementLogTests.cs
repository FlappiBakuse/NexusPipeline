using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Text;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class JudgementLogTests
{
    [Fact(DisplayName = "Growing A Native Log Tail Beyond Four MiB Retains Exactly The Known Suffix")]
    public void RetainsKnownTruncatedTail()
    {
        using var fixture = new PlanBuilderFixture();
        var blobs = new ImmutableBlobStore(fixture.Instance, new());
        using var reader = new JudgementLogReader();
        var primary = Binding(fixture.Instance.InstanceId);
        var line = new string('x', 1023) + "\n";
        var data = Encoding.UTF8.GetBytes("discarded\n" + string.Concat(Enumerable.Repeat(line, 4097)) + "end 雪");
        var parts = ImmutableArray.CreateBuilder<RawOutputPart>();
        for (var offset = 0; offset < data.Length; offset += 65536)
        {
            var content = blobs.PersistContent(data.AsMemory(offset, Math.Min(65536, data.Length - offset)), default);
            parts.Add(new(offset, ProcessOutputSource.StandardOutput, content));
        }
        var raw = new RawOutputManifest(RawOutputCodec.Format, 1, primary, Guid.NewGuid(), false, data.Length, 0,
            null, null, null, null, null, parts.ToImmutable());
        var growing = reader.Read(blobs, raw, false);
        Assert.True(growing.IsTruncated);
        Assert.Equal(string.Concat(Enumerable.Repeat(line, 4096)), growing.Text());
        var final = reader.Read(blobs, raw with { IsFinal = true, StandardOutputEofOffset = data.Length, StandardOutputEndOrder = 1,
            StandardErrorEofOffset = data.Length, StandardErrorEndOrder = 2 }, true);
        Assert.True(final.IsTruncated);
        Assert.Equal(line[8..] + string.Concat(Enumerable.Repeat(line, 4095)) + "end 雪\n", final.Text());
    }

    [Theory(DisplayName = "Incremental Judgement Tails Match Independent Replay Across Utf 8 Boundaries Eof Ordering And Final Partial Lines")]
    [InlineData(false)] [InlineData(true)]
    public void ReplaysGrowingLog(bool lateEnd)
    {
        using var fixture = new PlanBuilderFixture();
        var blobs = new ImmutableBlobStore(fixture.Instance, new());
        using var reader = new JudgementLogReader();
        var primary = Binding(fixture.Instance.InstanceId);
        var manifest = new RawOutputManifest(RawOutputCodec.Format, 1, primary, Guid.NewGuid(), false, 0, 0,
            null, null, null, null, null, []);
        var data = Encoding.UTF8.GetBytes("start 雪\r\n" + string.Concat(Enumerable.Repeat("row 界🙂\n", 2500)) + "tail 雪");
        for (var position = 0; position < data.Length;)
        {
            var count = Math.Min(997, data.Length - position);
            var content = blobs.PersistContent(data.AsMemory(position, count), default);
            manifest = manifest with { ObservedBytes = position + count, Parts = manifest.Parts.Add(new(position, ProcessOutputSource.StandardOutput, content)) };
            position += count;
            RequireEqual(manifest, false);
        }
        if (lateEnd)
        {
            var content = blobs.PersistContent("error\n"u8.ToArray(), default);
            var end = manifest.ObservedBytes;
            manifest = manifest with { ObservedBytes = end + content.SizeBytes, Parts = manifest.Parts.Add(new(end, ProcessOutputSource.StandardError, content)) };
            RequireEqual(manifest, false);
            manifest = manifest with { StandardOutputEofOffset = end, StandardOutputEndOrder = 1 };
        }
        manifest = manifest with { IsFinal = true, StandardOutputEofOffset = manifest.StandardOutputEofOffset ?? manifest.ObservedBytes,
            StandardOutputEndOrder = 1, StandardErrorEofOffset = manifest.ObservedBytes, StandardErrorEndOrder = 2 };
        RequireEqual(manifest, true); RequireEqual(manifest, true);
        var finalTail = reader.Read(blobs, manifest, true);
        var expected = "start 雪\n" + string.Concat(Enumerable.Repeat("row 界🙂\n", 2500)) + "tail 雪\n";
        Assert.Equal(lateEnd ? expected + "error\n" : expected, finalTail.Text());
        Assert.False(finalTail.IsTruncated);
        Assert.Throws<IOException>(() => File.WriteAllBytes(Path.Combine(fixture.Instance.RuntimeRoot, manifest.Parts[0].Content.RelativePath), [0]));
        var changed = manifest with { Parts = manifest.Parts.SetItem(0, manifest.Parts[0] with { Content = blobs.PersistContent("changed"u8.ToArray(), default) }) };
        Assert.Throws<IOException>(() => reader.Read(blobs, changed, true));

        void RequireEqual(RawOutputManifest raw, bool final)
        {
            var tail = new JudgementLogTail();
            Assert.True(RawOutputReplay.Read(blobs, raw, tail.AppendLine, final));
            var actual = reader.Read(blobs, raw, final);
            Assert.Equal(tail.Text(), actual.Text()); Assert.Equal(tail.IsTruncated, actual.IsTruncated);
        }
    }

    private static StepEvidenceBinding Binding(Guid instanceId) => new(instanceId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        "fixture", "Local\\NexusWorker-" + Guid.NewGuid().ToString("N"), Guid.NewGuid(), 1, new string('a', 64));
}
