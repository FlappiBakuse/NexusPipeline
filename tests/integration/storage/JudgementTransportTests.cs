using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Execution.Judgement;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class JudgementTransportTests
{
    [Theory(DisplayName = "Real Language Pipes Carry The Whole Escaped Or Replacement Decoded Body Without Widening Ordinary Frames")]
    [InlineData("javascript", false)]
    [InlineData("python", false)]
    [InlineData("javascript", true)]
    [InlineData("python", true)]
    public async Task TransfersBoundedBodies(string language, bool invalidUtf8)
    {
        using var http = new JudgementHttpFixture(invalidUtf8);
        await using var worker = new JudgementWorkerFixture(language, JudgementHttpFixture.Script(language),
            timeoutSeconds: 15, observeProbe: http.Observe);
        await worker.StartAsync();
        var invocation = worker.Invocation(1, true) with { ProbeEvidenceAllowance = JudgementProbeProtocol.MaximumEvidenceBytes };
        var exchange = await worker.Client.InvokeAsync(invocation, CancellationToken.None);
        await worker.AssertExitedAsync();
        Assert.Equal(1, http.Requests);
        Assert.Null(exchange.Response.FaultCode);
        Assert.Equal(BusinessOutcome.Succeeded, Assert.IsType<BusinessJudgementResult>(exchange.Response.Result).Outcome);
        var probe = Assert.Single(exchange.Probes);
        JudgementProbeProtocol.RequireExchanges(exchange.Probes, invocation);
        var body = probe.Reply.Result!.Value.GetProperty("body").GetString()!;
        Assert.Equal(2 * 1024 * 1024, body.Length);
        Assert.True(body.AsSpan().IndexOfAnyExcept(invalidUtf8 ? '\ufffd' : '<') < 0);
        var bytes = JudgementWorkerProtocol.Encode(probe);
        Assert.True(bytes.Length > 2 * 1024 * 1024);
        Assert.True(bytes.Length <= JudgementProbeProtocol.MaximumFrameBytes);
        Assert.Throws<JsonException>(() => JudgementWorkerProtocol.Decode<JudgementWorkerReady>(bytes));
        Assert.Equal(body, JudgementWorkerProtocol.Decode<JudgementProbeExchange>(bytes).Reply.Result!.Value.GetProperty("body").GetString());
    }

    [Theory(DisplayName = "An Actual Quota Fault Preserves The Requested Body Limit And The Next Final Call Gets Its Own Parent Allowance")]
    [InlineData("javascript")]
    [InlineData("python")]
    public async Task PreservesFinalReserve(string language)
    {
        using var http = new JudgementHttpFixture();
        await using var worker = new JudgementWorkerFixture(language, JudgementHttpFixture.Script(language),
            timeoutSeconds: 15, observeProbe: http.Observe);
        await worker.StartAsync();
        var first = worker.Invocation(1) with { ProbeEvidenceAllowance = JudgementProbeProtocol.FaultReserveBytes };
        var rejected = await worker.Client.InvokeAsync(first, CancellationToken.None);
        var fault = Assert.Single(rejected.Probes);
        Assert.Equal(2 * 1024 * 1024, fault.Request.Options.GetProperty("maxBytes").GetInt32());
        Assert.Equal("judgement_probe_budget_exceeded", fault.Reply.FaultCode);
        Assert.Null(fault.Reply.Result);
        Assert.Null(rejected.Response.Result);
        Assert.True(JudgementWorkerProtocol.Encode(fault).LongLength <= first.ProbeEvidenceAllowance);
        var final = worker.Invocation(2, true) with { ProbeEvidenceAllowance = JudgementProbeProtocol.MaximumEvidenceBytes };
        var completed = await worker.Client.InvokeAsync(final, CancellationToken.None);
        await worker.AssertExitedAsync();
        Assert.Equal(2, http.Requests);
        Assert.Null(completed.Response.FaultCode);
        Assert.Equal(BusinessOutcome.Succeeded, Assert.IsType<BusinessJudgementResult>(completed.Response.Result).Outcome);
        JudgementProbeProtocol.RequireExchanges(completed.Probes, final);
        Assert.Equal(2, Assert.Single(completed.Probes).Request.Generation);
    }

    [Theory(DisplayName = "A Worker Without A Frozen Host Http Observer Reports Unavailable Instead Of Opening A Direct Client")]
    [InlineData("javascript")]
    [InlineData("python")]
    public async Task RejectsDirectHttp(string language)
    {
        await using var worker = new JudgementWorkerFixture(language, JudgementHttpFixture.Script(language), timeoutSeconds: 10);
        await worker.StartAsync();
        var exchange = await worker.InvokeAsync(1, true);
        await worker.AssertExitedAsync();
        Assert.Null(exchange.Response.Result);
        Assert.Equal("judgement_probe_unavailable", Assert.Single(exchange.Probes).Reply.FaultCode);
    }
    [Theory(DisplayName = "The Parent Invocation Deadline Cancels An In Flight Async Observation Before Any Reply Is Authorized")]
    [InlineData("javascript")]
    [InlineData("python")]
    public async Task CancelsExpiredObservation(string language)
    {
        var entered = 0; var cancelled = 0;
        async Task<JudgementProbeReply> Observe(JudgementProbeRequest request, CancellationToken token)
        {
            Volatile.Write(ref entered, 1);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { Volatile.Write(ref cancelled, 1); throw; }
            throw new IOException("unreachable_probe_result");
        }
        await using var worker = new JudgementWorkerFixture(language, JudgementHttpFixture.Script(language),
            timeoutSeconds: 5, observeProbe: Observe);
        await worker.StartAsync();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.Client.InvokeAsync(worker.Invocation(1, true), CancellationToken.None));
        Assert.Equal(1, Volatile.Read(ref entered));
        Assert.Equal(1, Volatile.Read(ref cancelled));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Empty(worker.Client.Probes);
    }
}
