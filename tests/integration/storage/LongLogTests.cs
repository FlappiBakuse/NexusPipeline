using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class LongLogTests(ITestOutputHelper output)
{
    private const int Blocks = 832;
    private const int BlockBytes = 65536;
    private const int SourceBytes = Blocks * BlockBytes;

    [Theory(DisplayName = "Real Fifty Two Mi B Stdout And File Originals Survive Capture Catalog And Bounded Streaming")]
    [InlineData("long-stdout")]
    [InlineData("long-file")]
    public async Task StreamsCompleteOriginal(string mode)
    {
        await using var fixture = new FileLogFixture(mode);
        fixture.Prepare(); await fixture.Start(); await fixture.WaitExit();
        var document = await fixture.Collect();
        var blobs = new ImmutableBlobStore(fixture.Source.Instance, new());
        var raw = RawOutputCodec.Read(blobs, fixture.Binding, document.RawOutput!);
        Assert.True(raw.IsComplete);
        Assert.Equal(0, raw.DiscardedBytes);
        Assert.Equal(SourceBytes + "pipe-success\n"u8.Length, raw.ObservedBytes);
        Assert.Equal(raw.ObservedBytes, raw.Parts.Sum(part => part.Content.SizeBytes));
        Assert.InRange(raw.Parts.Length, Blocks, RawOutputCodec.MaximumParts);
        Assert.All(raw.Parts, part => Assert.InRange(part.Content.SizeBytes, 1, BlockBytes));
        var sourceBytes = raw.Parts.Where(part => mode == "long-file" ? part.FileSourceId is not null : part.FileSourceId is null)
            .Sum(part => part.Content.SizeBytes);
        Assert.Equal(mode == "long-file" ? SourceBytes : raw.ObservedBytes, sourceBytes);

        await fixture.Complete();
        var receipt = new AttemptReceiptStore(fixture.Source.Database).Read(fixture.Binding.AttemptId)!;
        new LogCatalogProjection(fixture.Source.Instance, fixture.Source.Database, new(), fixture.Source.Lease).OnCommitted(receipt);
        var key = new RunLogKey(fixture.Binding.RunId, fixture.Binding.AttemptId, fixture.Binding.StepId);
        var row = Assert.Single(new LogCatalogReader(fixture.Source.Instance, fixture.Source.Database, new())
            .ReadPage(new(fixture.Binding.RunId, fixture.Binding.AttemptId)).Items);
        Assert.Equal(key, row.Key);
        Assert.Equal(raw.ObservedBytes, row.RecordedBytes);
        Assert.Equal(0, row.DiscardedBytes);
        Assert.True(row.IsComplete);

        var reader = new ExecutionLogReader(fixture.Source.Instance, fixture.Source.Database, new());
        var description = reader.Describe(key);
        Assert.Equal(row.SnapshotSha256, description.SnapshotSha256);
        Assert.Equal(raw.ObservedBytes, description.RecordedBytes);
        var paths = description.Parts.Select(part => Path.Combine(fixture.Source.Instance.RuntimeRoot,
            "blobs", "content", part.Sha256 + ".bin")).Distinct().ToArray();
        Assert.InRange(paths.Length, 1, RawOutputCodec.MaximumParts);
        using var process = Process.GetCurrentProcess();
        process.Refresh(); var handlesBefore = process.HandleCount;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        using var original = reader.OpenOriginal(key, description.SnapshotSha256);
        var allocatedToOpen = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        process.Refresh(); var handlesOpen = process.HandleCount;
        Assert.InRange(allocatedToOpen, 0, 32L * 1024 * 1024);
        Assert.Equal(raw.ObservedBytes, original.Length);
        Assert.False(original.CanSeek);
        foreach (var path in new[] { paths[0], paths[paths.Length / 2], paths[^1] })
            Assert.Throws<IOException>(() => File.Delete(path));

        using var expectedPipe = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var expectedFile = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        expectedPipe.AppendData("pipe-success\n"u8);
        var block = new byte[BlockBytes];
        for (var index = 0; index < Blocks; index++)
        {
            FillBlock(block, index, mode == "long-file" ? (byte)'f' : (byte)'p');
            (mode == "long-file" ? expectedFile : expectedPipe).AppendData(block);
        }
        using var actualPipe = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var actualFile = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long received = 0;
        foreach (var part in description.Parts)
        {
            var remaining = checked((int)part.SizeBytes);
            var destination = part.Source == "file" ? actualFile : actualPipe;
            Assert.True(part.Source is "file" or "standardOutput");
            while (remaining > 0)
            {
                var count = await original.ReadAsync(block.AsMemory(0, remaining));
                Assert.True(count > 0);
                destination.AppendData(block, 0, count);
                received += count; remaining -= count;
            }
        }
        Assert.Equal(0, await original.ReadAsync(block));
        Assert.Equal(raw.ObservedBytes, received);
        Assert.Equal(Convert.ToHexStringLower(expectedPipe.GetHashAndReset()), Convert.ToHexStringLower(actualPipe.GetHashAndReset()));
        Assert.Equal(Convert.ToHexStringLower(expectedFile.GetHashAndReset()), Convert.ToHexStringLower(actualFile.GetHashAndReset()));
        original.Dispose();
        foreach (var path in paths)
            using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.True(exclusive.Length > 0);
        process.Refresh();
        output.WriteLine($"P2_LONG_LOG_DATASET mode={mode} runtime={fixture.Source.Instance.RuntimeRoot} " +
            $"runId={fixture.Binding.RunId:D} attemptId={fixture.Binding.AttemptId:D}");
        output.WriteLine($"mode={mode} parts={description.Parts.Count} distinctBlobs={paths.Length} " +
            $"handlesBefore={handlesBefore} handlesOpen={handlesOpen} handlesAfter={process.HandleCount} " +
            $"allocatedToOpen={allocatedToOpen} streamedBytes={received}");
    }

    private static void FillBlock(byte[] block, int index, byte content)
    {
        Array.Fill(block, content);
        Encoding.ASCII.GetBytes(index.ToString("D8"), block.AsSpan(0, 8));
        for (var end = 4095; end < block.Length; end += 4096) block[end] = (byte)'\n';
    }
}
