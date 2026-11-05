using System.Runtime.Versioning;
using System.Text;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class LogReaderTests
{
    [Theory(DisplayName = "Actual Main And Hook Logs Keep Their Distinct Committed Roles And Original Receipt")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeepsCommittedRoles(bool configuration)
    {
        using var fixture = new ExecutionHooksFixture(configuration);
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        var reader = new ExecutionLogReader(fixture.Source.Instance, fixture.Source.Database, new());
        var step = Assert.Single(fixture.State.Total.Steps).StepId;
        var report = reader.ReadReport(receipt.Facts.RunId, receipt.Facts.AttemptId);
        Assert.Equal(receipt.OperationId, report.OperationId);
        Assert.Equal(receipt.Facts.AttemptId, report.AttemptId);
        Assert.False(report.CanCancel);
        Assert.Equal(step, Assert.Single(report.Steps).StepId);
        Assert.NotNull(report.Steps[0].Engine);
        Assert.ThrowsAny<IOException>(() => reader.ReadReport(Guid.NewGuid(), receipt.Facts.AttemptId));
        foreach (var role in Enum.GetValues<RunLogRole>())
        {
            var key = new RunLogKey(receipt.Facts.RunId, receipt.Facts.AttemptId, step, role);
            var description = reader.Describe(key);
            Assert.True(description.IsComplete); Assert.Equal(0, description.DiscardedBytes);
            Assert.InRange(description.RecordedBytes, 1, 65536);
            using var original = reader.OpenOriginal(key, description.SnapshotSha256);
            using var text = new StreamReader(original, Encoding.UTF8);
            var value = await text.ReadToEndAsync();
            Assert.Contains(role == RunLogRole.Main ? "done" : "failed", value);
            Assert.DoesNotContain(role == RunLogRole.Main ? "failed" : "done", value);
            var tail = reader.ReadTail(key, 4);
            Assert.Equal(description.SnapshotSha256, tail.Description.SnapshotSha256);
            Assert.InRange(tail.Slices.Sum(slice => slice.Bytes.Length), 1, 4);
            Assert.True(tail.PrefixOmitted);
        }
        Assert.Equal(receipt, new AttemptReceiptStore(fixture.Source.Database).Read(receipt.Facts.AttemptId));
    }

    [Fact(DisplayName = "A Bounded Slow Original Reader Keeps Utf 8 Source Bytes And Pins Every Part Without Holding Sql")]
    public async Task StreamsPinnedParts()
    {
        using var fixture = new ExecutionRuntimeFixture(mode: "history-log-stream");
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        var reader = new ExecutionLogReader(fixture.Source.Instance, fixture.Source.Database, new());
        var key = new RunLogKey(receipt.Facts.RunId, receipt.Facts.AttemptId, fixture.State.Total.Steps[0].StepId);
        var description = reader.Describe(key);
        Assert.True(description.IsComplete); Assert.True(description.RecordedBytes > 65536);
        var tail = reader.ReadTail(key, 257);
        Assert.True(tail.PrefixOmitted); Assert.InRange(tail.Slices.Count, 1, 32);
        Assert.Equal(257, tail.Slices.Sum(slice => slice.Bytes.Length));
        var input = reader.OpenOriginal(key, description.SnapshotSha256);
        Assert.False(input.CanSeek); Assert.False(input.CanWrite);
        Assert.Equal(description.RecordedBytes, input.Length);
        var paths = description.Parts.Select(part => Path.Combine(fixture.Source.Instance.RuntimeRoot,
            "blobs", "content", part.Sha256 + ".bin")).Distinct().ToArray();
        foreach (var path in paths) Assert.Throws<IOException>(() => File.Delete(path));
        fixture.Base.Execute("CREATE TABLE log_reader_independent_write(value TEXT); INSERT INTO log_reader_independent_write VALUES('written');");
        var expected = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(new string('a', 1023) + "\n", 63)) +
            new string('a', 1023) + "🙂" + string.Concat(Enumerable.Repeat(new string('b', 1023) + "\n", 63)) + "tail");
        Assert.Equal(65536, description.Parts[0].SizeBytes);
        var buffer = new byte[4096]; long observed = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer);
            if (count == 0) break;
            Assert.True(buffer.AsSpan(0, count).SequenceEqual(expected.AsSpan(checked((int)observed), count)));
            observed += count; await Task.Delay(1);
        }
        Assert.Equal(expected.LongLength, observed);
        input.Dispose();
        foreach (var path in paths)
        {
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            Assert.True(exclusive.Length > 0);
        }
        fixture.Base.Execute("DROP TABLE log_reader_independent_write;");
        Assert.Equal(receipt, new AttemptReceiptStore(fixture.Source.Database).Read(receipt.Facts.AttemptId));
    }

    [Theory(DisplayName = "A Foreign Log Selection Or Changed Original Cannot Grant An Arbitrary File Read")]
    [InlineData("run")]
    [InlineData("attempt")]
    [InlineData("step")]
    [InlineData("snapshot")]
    [InlineData("missing")]
    [InlineData("tampered")]
    public async Task RejectsForeignSelections(string fault)
    {
        using var fixture = new ExecutionHooksFixture(false);
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        var reader = new ExecutionLogReader(fixture.Source.Instance, fixture.Source.Database, new());
        var key = new RunLogKey(receipt.Facts.RunId, receipt.Facts.AttemptId, fixture.State.Total.Steps[0].StepId);
        var description = reader.Describe(key);
        var request = fault switch
        {
            "run" => key with { RunId = Guid.NewGuid() },
            "attempt" => key with { AttemptId = Guid.NewGuid() },
            "step" => key with { StepId = "unrelated-step" },
            _ => key
        };
        var path = Path.Combine(fixture.Source.Instance.RuntimeRoot, "blobs", "content", description.Parts[^1].Sha256 + ".bin");
        if (fault == "missing") File.Delete(path);
        if (fault == "tampered") File.WriteAllText(path, "unknown-content-preserve");
        Assert.ThrowsAny<IOException>(() =>
        {
            using var ignored = reader.OpenOriginal(request, fault == "snapshot" ? new string('0', 64) : description.SnapshotSha256);
        });
        Assert.Equal(receipt, new AttemptReceiptStore(fixture.Source.Database).Read(receipt.Facts.AttemptId));
        if (fault == "tampered") Assert.Equal("unknown-content-preserve", File.ReadAllText(path));
    }

    [Theory(DisplayName = "Actual File Epochs And Offsets Survive Historical Streaming Without Including Their Baseline")]
    [InlineData("replace")]
    [InlineData("rewrite")]
    [InlineData("split")]
    public async Task StreamsSavedEpochs(string mode)
    {
        await using var fixture = new FileLogFixture(mode);
        fixture.Prepare(); await fixture.Start(); await fixture.WaitExit(); _ = await fixture.Collect(); await fixture.Complete();
        var key = new RunLogKey(fixture.Binding.RunId, fixture.Binding.AttemptId, fixture.Binding.StepId);
        var reader = new ExecutionLogReader(fixture.Source.Instance, fixture.Source.Database, new());
        var description = reader.Describe(key);
        Assert.True(description.IsComplete);
        Assert.Contains(description.Parts, part => part.Source == "file" && part.FileSourceId is not null && part.FileOffset is not null);
        using var input = reader.OpenOriginal(key, description.SnapshotSha256);
        using var text = new StreamReader(input, Encoding.UTF8);
        var value = await text.ReadToEndAsync();
        Assert.DoesNotContain("old-file-success", value);
        Assert.Contains("pipe-success", value);
        Assert.Contains(mode == "split" ? "file-中文🙂-failure" : "file-failure", value);
        var tail = reader.ReadTail(key, 9);
        Assert.InRange(tail.Slices.Sum(slice => slice.Bytes.Length), 1, 9);
        Assert.All(tail.Slices.Where(slice => slice.Source == "file"), slice => Assert.NotNull(slice.FileOffset));
    }
}
