using System.Text.Json;
using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Execution.Judgement;
using NexusPipeline.TestFixtures;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class JudgementTransportTests
{
    [Fact(DisplayName = "The Actual Broker Rejects Validly Bound Replies That Exceed The Cumulative Allowance")]
    public async Task RejectsOversizedProbePrefix()
    {
        const string source = """
function nexusJudge() {
  for (let index = 0; index < 2; index++) {
    const result = nexus.probes.httpGet({url:'https://controlled.invalid/body',maxBytes:196608});
    if (result.body.length !== 196608) throw new Error('incomplete reply');
  }
  return {outcome:'succeeded',reason:'only allowed if the whole prefix fits'};
}
""";
        await using var worker = new JudgementWorkerFixture("javascript", source, timeoutSeconds: 10);
        await worker.StartAsync();
        var invocation = worker.Invocation(1, true) with { ProbeEvidenceAllowance = 350_000 };
        var body = JsonSerializer.SerializeToElement(new { ok = true, status = 200, body = new string('a', 196_608),
            truncated = false, error = (string?)null });
        var response = await worker.SendProbeRepliesAsync(invocation, 2, request => JudgementProbeProtocol.Success(request, body));
        Assert.Equal("judgement_worker_execution_failed", response.FaultCode);
        Assert.Null(response.Result);
        Assert.Equal(2, response.ProbeCount);
        await worker.AssertExitedAsync();
    }

    [Theory(DisplayName = "Real Interpreters Receive The Whole Escaped Or Replacement Decoded Body")]
    [InlineData("javascript", false)]
    [InlineData("python", false)]
    [InlineData("javascript", true)]
    [InlineData("python", true)]
    public async Task TransfersBoundedBodies(string language, bool invalidUtf8)
    {
        using var http = new JudgementHttpFixture(invalidUtf8);
        await using var worker = new JudgementWorkerFixture(language, JudgementHttpFixture.Script(language, invalidUtf8: invalidUtf8),
            timeoutSeconds: 15, observeProbe: http.Observe);
        await worker.StartAsync();
        var invocation = worker.Invocation(1, true) with { ProbeEvidenceAllowance = JudgementProbeProtocol.MaximumEvidenceBytes };
        var exchange = await worker.InvokeAsync(invocation, CancellationToken.None);
        await worker.AssertExitedAsync();
        Assert.Equal(1, http.Requests);
        Assert.Equal(BusinessOutcome.Succeeded, Assert.IsType<BusinessJudgementResult>(exchange.Response.Result).Outcome);
        Assert.Single(exchange.Probes);
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
        var rejected = await worker.InvokeAsync(first, CancellationToken.None);
        var fault = Assert.Single(rejected.Probes);
        Assert.Equal(2 * 1024 * 1024, fault.Request.Options.GetProperty("maxBytes").GetInt32());
        Assert.Equal("judgement_probe_budget_exceeded", fault.Reply.FaultCode);
        Assert.Null(fault.Reply.Result);
        Assert.Null(rejected.Response.Result);
        var final = worker.Invocation(2, true) with { ProbeEvidenceAllowance = JudgementProbeProtocol.MaximumEvidenceBytes };
        var completed = await worker.InvokeAsync(final, CancellationToken.None);
        await worker.AssertExitedAsync();
        Assert.Equal(2, http.Requests);
        Assert.Equal(BusinessOutcome.Succeeded, Assert.IsType<BusinessJudgementResult>(completed.Response.Result).Outcome);
        Assert.Equal(2, Assert.Single(completed.Probes).Request.Generation);
    }

    [Theory(DisplayName = "A Worker Without A Frozen Host Http Observer Reports Unavailable Instead Of Opening A Direct Client")]
    [InlineData("javascript")]
    [InlineData("python")]
    public async Task RejectsDirectHttp(string language)
    {
        await using var endpoint = new NativeHttpFixture(_ => new(200, "reachable"u8.ToArray()));
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        Assert.Equal("reachable", await client.GetStringAsync(new Uri(endpoint.Address, "positive")));
        var target = new Uri(endpoint.Address, "direct");
        var script = JudgementHttpFixture.Script(language).Replace("https://controlled.invalid/body", target.AbsoluteUri, StringComparison.Ordinal);
        await using var worker = new JudgementWorkerFixture(language, script, timeoutSeconds: 10);
        await worker.StartAsync();
        var exchange = await worker.InvokeAsync(1, true);
        await worker.AssertExitedAsync();
        Assert.Null(exchange.Response.Result);
        Assert.Equal("judgement_probe_unavailable", Assert.Single(exchange.Probes).Reply.FaultCode);
        Assert.Equal("/positive", Assert.Single(endpoint.Requests).Target);
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
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.InvokeAsync(worker.Invocation(1, true), CancellationToken.None));
        Assert.Equal(1, Volatile.Read(ref entered));
        Assert.Equal(1, Volatile.Read(ref cancelled));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Empty(worker.Client.Probes);
    }
}
