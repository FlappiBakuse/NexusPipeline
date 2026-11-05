using System.Runtime.Versioning;
using System.Text;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class RawOutputTests
{
    [Fact(DisplayName = "A Real Four Byte Utf 8 Character Across Full Raw Parts Replays Without Overflow And The Unpublished Bytes Remain Leased")]
    public async Task PreservesUtf8Parts()
    {
        using var fixture = new StepWriterFixture("utf8-boundary");
        var state = fixture.Scope.VerifyReadOnly(); var binding = StepResultCodec.Binding(state.Latest.Root, Assert.Single(state.Steps));
        var source = fixture.Base.Source; var blobs = new ImmutableBlobStore(source.Instance, new());
        using var raw = new RawOutputRecorder(blobs, binding);
        var capture = new ProcessOutputCapture(fixture.Output, raw); var lines = Collect(capture);
        fixture.Scope.EnterApplying(); fixture.Writer.Start(fixture.Output);
        Assert.Equal(23, await fixture.Writer.AwaitRootAsync(TimeSpan.FromSeconds(10)));
        var drain = await capture.DrainAsync(TimeSpan.FromSeconds(10)); Assert.False(drain.DeadlineExceeded);
        fixture.AssertClosed(await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10)), true);
        _ = await lines.WaitAsync(TimeSpan.FromSeconds(10));
        var artifact = await raw.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var manifest = RawOutputCodec.Read(blobs, binding, artifact);
        Assert.True(manifest.IsComplete); Assert.Equal(131072, manifest.ObservedBytes); Assert.Equal(2, manifest.Parts.Length);
        Assert.All(manifest.Parts, part => Assert.Equal(65536, part.Content.SizeBytes));
        var count = 0; var emojiLines = 0;
        Assert.True(RawOutputReplay.Read(blobs, manifest, line =>
        {
            count++;
            if (line.Contains("🙂", StringComparison.Ordinal)) { Assert.Equal("a🙂b", line); emojiLines++; }
        }));
        Assert.Equal(65534, count); Assert.Equal(1, emojiLines);
        var path = Path.Combine(source.Instance.RuntimeRoot, manifest.Parts[0].Content.RelativePath);
        var before = File.ReadAllBytes(path);
        Assert.Throws<IOException>(() => File.Delete(path)); Assert.Equal(before, File.ReadAllBytes(path));
        raw.Dispose();
        using var released = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Equal(before.LongLength, released.Length);
    }

    [Theory(DisplayName = "The Single Native Reader Keeps Original Bytes While Decoding Split Utf 8 And Cr Lf Into Its Bounded Lines")]
    [InlineData("tail")]
    [InlineData("split")]
    public async Task DecodesBoundedLines(string mode)
    {
        using var fixture = new StepWriterFixture(mode);
        using var raw = new RawSink(mode == "split" ? fixture.Marker : null);
        var capture = new ProcessOutputCapture(fixture.Output, raw); var lines = Collect(capture);
        fixture.Scope.EnterApplying(); fixture.Writer.Start(fixture.Output);
        Assert.Equal(37, await fixture.Writer.AwaitRootAsync(TimeSpan.FromSeconds(10)));
        Assert.True((await capture.DrainAsync(TimeSpan.FromSeconds(10))).IsComplete);
        fixture.AssertClosed(await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10)), true);
        var observed = await lines.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(mode == "split" ? "first\r\n中文🙂\r\nlast" : "ready\n中文尾行", Encoding.UTF8.GetString(raw.Output.ToArray()));
        Assert.Equal(mode == "split" ? "error\r\n尾" : "error\n错误尾行", Encoding.UTF8.GetString(raw.Error.ToArray()));
        Assert.Equal(mode == "split" ? new[] { "first", "中文🙂", "last" } : new[] { "ready", "中文尾行" },
            observed.Where(line => line.Source == ProcessOutputSource.StandardOutput).Select(line => line.Text));
        Assert.True(raw.OutputEof); Assert.True(raw.ErrorEof); Assert.All(observed, line => Assert.True(line.IsContinuous));
        if (mode == "split") Assert.Equal(raw.Output.Length, raw.OutputFrames);
    }

    [Fact(DisplayName = "Invalid Utf 8 Remains In The Actual Raw Evidence And The Producer Still Drains To Real Eof")]
    public async Task DrainsInvalidBytes()
    {
        using var fixture = new StepWriterFixture("invalid"); using var raw = new RawSink();
        var capture = new ProcessOutputCapture(fixture.Output, raw); var lines = Collect(capture);
        fixture.Scope.EnterApplying(); fixture.Writer.Start(fixture.Output);
        Assert.Equal(19, await fixture.Writer.AwaitRootAsync(TimeSpan.FromSeconds(10)));
        var drained = await capture.DrainAsync(TimeSpan.FromSeconds(10));
        Assert.False(drained.IsComplete); Assert.Equal("process_output_encoding_invalid", drained.FailureCode);
        fixture.AssertClosed(await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10)), true);
        Assert.Equal(1048578, raw.Output.Length); Assert.Equal(255, raw.Output.GetBuffer()[0]); Assert.Equal(10, raw.Output.GetBuffer()[1]);
        Assert.True(raw.Output.GetBuffer().AsSpan(2, 1048576).IndexOfAnyExcept((byte)0) < 0);
        Assert.Equal("error-tail", Encoding.UTF8.GetString(raw.Error.ToArray())); Assert.True(raw.OutputEof); Assert.True(raw.ErrorEof);
        Assert.DoesNotContain(await lines.WaitAsync(TimeSpan.FromSeconds(10)), line => line.Source == ProcessOutputSource.StandardOutput);
    }

    [Fact(DisplayName = "A Rejected Raw Sink Cannot Block The Owned Producer Or Be Reported As Complete Evidence")]
    public async Task RejectsIncompleteSinks()
    {
        using var fixture = new StepWriterFixture("tail"); using var raw = new RawSink() { Reject = true };
        var capture = new ProcessOutputCapture(fixture.Output, raw); var lines = Collect(capture);
        fixture.Scope.EnterApplying(); fixture.Writer.Start(fixture.Output);
        Assert.Equal(37, await fixture.Writer.AwaitRootAsync(TimeSpan.FromSeconds(10)));
        var drained = await capture.DrainAsync(TimeSpan.FromSeconds(10));
        Assert.False(drained.IsComplete); Assert.Equal("process_output_raw_unavailable", drained.FailureCode);
        Assert.True(drained.StandardOutputEof); Assert.True(drained.StandardErrorEof);
        fixture.AssertClosed(await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10)), true);
        Assert.All(await lines.WaitAsync(TimeSpan.FromSeconds(10)), line => Assert.False(line.IsContinuous));
    }

    private static async Task<List<ProcessOutputLine>> Collect(ProcessOutputCapture capture)
    {
        var lines = new List<ProcessOutputLine>();
        await foreach (var line in capture.Lines.ReadAllAsync()) lines.Add(line);
        return lines;
    }

    private sealed class RawSink(string? acknowledgement = null) : IOutputByteSink, IDisposable
    {
        internal MemoryStream Output { get; } = new();
        internal MemoryStream Error { get; } = new();
        internal bool Reject { get; init; }
        internal bool OutputEof { get; private set; }
        internal bool ErrorEof { get; private set; }
        internal int OutputFrames { get; private set; }
        public bool TryWrite(ProcessOutputSource source, ReadOnlySpan<byte> bytes)
        {
            if (Reject) return false;
            if (source == ProcessOutputSource.StandardOutput)
            {
                Output.Write(bytes); OutputFrames++;
                if (acknowledgement is not null) File.WriteAllBytes(acknowledgement + ".ack-" + Output.Length.ToString("D4"), []);
            }
            else Error.Write(bytes);
            return true;
        }
        public void Complete(ProcessOutputSource source, bool eof)
        { if (source == ProcessOutputSource.StandardOutput) OutputEof = eof; else ErrorEof = eof; }
        public void Dispose() { Output.Dispose(); Error.Dispose(); }
    }
}
