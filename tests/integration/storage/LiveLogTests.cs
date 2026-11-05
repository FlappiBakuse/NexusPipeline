using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class LiveLogTests
{
    [Fact(DisplayName = "Actual Owned Output Keeps Its Raw Evidence When A Slow Live Subscriber Is Disconnected")]
    public async Task PreservesDisconnectedEvidence()
    {
        using var fixture = new StepWriterFixture("live-log-stream");
        var source = fixture.Base.Source;
        var state = fixture.Scope.VerifyReadOnly();
        var binding = StepResultCodec.Binding(state.Latest.Root, Assert.Single(state.Steps));
        using var live = source.Lease.LiveLogs.Register(source.Instance, source.Database, new(), binding);
        using var raw = new RawOutputRecorder(new(source.Instance, new()), binding, live: live);
        var capture = new ProcessOutputCapture(fixture.Output, raw);
        var lines = Drain(capture);
        var reader = new LiveLogReader(source.Instance, source.Lease);
        var key = new RunLogKey(binding.RunId, binding.AttemptId, binding.StepId);
        using var slow = reader.Subscribe(key); using var fast = reader.Subscribe(key);
        Assert.Throws<IOException>(() => reader.Subscribe(key with { RunId = Guid.NewGuid() }));
        var consumed = Consume();
        fixture.Scope.EnterApplying(); fixture.Writer.Start(fixture.Output);
        try
        {
            Assert.Equal(37, await fixture.Writer.AwaitRootAsync(TimeSpan.FromSeconds(20)));
            Assert.True((await capture.DrainAsync(TimeSpan.FromSeconds(10))).IsComplete);
        }
        finally { fixture.AssertClosed(await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10)), true); }
        await lines;
        var manifest = RawOutputCodec.Read(new(source.Instance, new()), binding, await raw.Completion);
        Assert.True(manifest.IsComplete); Assert.Equal(80 * 1024, manifest.ObservedBytes); Assert.Equal(0, manifest.DiscardedBytes);
        Assert.Equal("run_log_subscription_gap", slow.FailureCode);
        var result = await consumed.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(80 * 1024, result.Bytes);
        var line = Encoding.UTF8.GetBytes(new string('a', 1023) + "\n");
        using var expected = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var index = 0; index < 80; index++) expected.AppendData(line);
        Assert.Equal(Convert.ToHexStringLower(expected.GetHashAndReset()), result.Hash);
        var tail = reader.ReadTail(key); Assert.True(tail.PrefixOmitted); Assert.True(tail.IsComplete);
        Assert.InRange(tail.Frames.Count, 1, 32); Assert.InRange(tail.Frames.Sum(frame => frame.Bytes.Length), 1, 65536);
        raw.Dispose(); Assert.Throws<IOException>(() => reader.ReadTail(key));

        async Task<(long Bytes, string Hash)> Consume()
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); long observed = 0;
            await foreach (var frame in fast.Events.ReadAllAsync())
            {
                Assert.Equal(observed, frame.ObservedOffset); Assert.False(frame.GapBefore); Assert.Equal("stdout", frame.Source);
                hash.AppendData(frame.Bytes); observed += frame.Bytes.Length;
                if (observed % 1024 == 0) File.WriteAllBytes(fixture.Marker + ".live-ack-" + observed / 1024, []);
            }
            return (observed, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
    }

    [Theory(DisplayName = "An Actual Active Hook Tail Cannot Be Read As Main Or Another Attempt")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BindsActiveRoles(bool configuration)
    {
        using var fixture = new ExecutionHooksFixture(configuration, waitPre: true);
        var running = fixture.Consumer.RunNextAsync();
        await fixture.Ready("pre");
        var state = fixture.State;
        var key = new RunLogKey(Guid.Parse(state.Attempt.RunId), Guid.Parse(state.Attempt.AttemptId),
            Assert.Single(state.Total.Steps).StepId, RunLogRole.PreHook);
        var reader = new LiveLogReader(fixture.Source.Instance, fixture.Source.Lease);
        var timer = Stopwatch.StartNew(); LiveLogTail tail;
        do { tail = reader.ReadTail(key); if (tail.Frames.Count != 0) break; await Task.Delay(10); }
        while (timer.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Contains("failed", Encoding.UTF8.GetString(tail.Frames.SelectMany(frame => frame.Bytes).ToArray()));
        Assert.False(tail.IsComplete);
        Assert.Throws<IOException>(() => reader.ReadTail(key with { Role = RunLogRole.Main }));
        Assert.Throws<IOException>(() => reader.ReadTail(key with { AttemptId = Guid.NewGuid() }));
        using var subscription = reader.Subscribe(key);
        File.WriteAllBytes(fixture.Marker + ".pre.release", []);
        var receipt = Assert.IsType<AttemptReceipt>(await running.WaitAsync(TimeSpan.FromSeconds(30)));
        await subscription.Events.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Throws<IOException>(() => reader.ReadTail(key));
        var historical = new ExecutionLogReader(fixture.Source.Instance, fixture.Source.Database, new()).ReadTail(key);
        Assert.True(historical.Description.IsComplete); Assert.Equal(receipt.Facts.AttemptId, historical.Description.Key.AttemptId);
    }

    private static async Task Drain(ProcessOutputCapture capture)
    { await foreach (var _ in capture.Lines.ReadAllAsync()) { } }
}
