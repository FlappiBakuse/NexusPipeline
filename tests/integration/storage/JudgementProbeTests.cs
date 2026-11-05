using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Execution.Judgement;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class JudgementProbeTests
{
    [Theory(DisplayName = "A Hung Native Window Cannot Block The Parent Probe Or Invent A Window Observation")]
    [InlineData("javascript")]
    [InlineData("python")]
    public async Task BoundsHungProbes(string language)
    {
        using var window = new ProbeWindowFixture();
        var source = language == "javascript"
            ? "function nexusJudge(input) { nexus.probes.windows({pid:input.pid}); return {outcome:'succeeded',reason:'unreachable'}; }"
            : "def nexus_judge(input):\n    nexus.probes.windows({'pid':input['pid']})\n    return {'outcome':'succeeded','reason':'unreachable'}";
        await using var fixture = new JudgementWorkerFixture(language, source, timeoutSeconds: 10);
        await fixture.StartAsync(); window.Pause();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var exchange = await fixture.InvokeAsync(1, true, JsonSerializer.Serialize(new { pid = Environment.ProcessId }));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3));
        await fixture.AssertExitedAsync();
        Assert.Null(exchange.Response.Result); Assert.Equal("judgement_worker_execution_failed", exchange.Response.FaultCode);
        var probe = Assert.Single(exchange.Probes);
        Assert.Null(probe.Reply.Result); Assert.Equal("judgement_probe_unavailable", probe.Reply.FaultCode);
    }

    [Theory(DisplayName = "Real Language Workers Observe The Controlled Native Process And Visible Window Through One Invocation Bound Channel")]
    [InlineData("javascript")]
    [InlineData("python")]
    public async Task BindsNativeObservations(string language)
    {
        using var window = new ProbeWindowFixture();
        var source = language == "javascript" ? """
function nexusJudge(input) {
  const p = nexus.probes.processes({pid:input.pid});
  const w = nexus.probes.windows({pid:input.pid,titleContains:input.title});
  if (!p.processes.some(value=>value.pid===input.pid) || !w.windows.some(value=>value.title===input.title))
    throw new Error('actual observation missing');
  if (typeof nexus.probes.processes.GetType !== 'undefined') throw new Error('CLR wrapper exposed');
  return {outcome:'succeeded',reason:'actual process and window'};
}
""" : """
def nexus_judge(input):
    p = nexus.probes.processes({'pid':input['pid']})
    w = nexus.probes.windows({'pid':input['pid'],'titleContains':input['title']})
    assert any(value['pid'] == input['pid'] for value in p['processes'])
    assert any(value['title'] == input['title'] for value in w['windows'])
    return {'outcome':'succeeded','reason':'actual process and window'}
""";
        await using var fixture = new JudgementWorkerFixture(language, source, timeoutSeconds: 10);
        await fixture.StartAsync();
        var exchange = await fixture.InvokeAsync(1, true, JsonSerializer.Serialize(new { pid = Environment.ProcessId, title = window.Title }));
        await fixture.AssertExitedAsync();
        Assert.Null(exchange.Response.FaultCode); Assert.Equal(BusinessOutcome.Succeeded, exchange.Response.Result!.Outcome);
        Assert.Equal(2, exchange.Probes.Count); Assert.Equal(2, exchange.Response.ProbeCount);
        Assert.Equal(JudgementProbeProtocol.Hash(exchange.Probes), exchange.Response.ProbeTranscriptSha256);
        JudgementProbeProtocol.RequireExchanges(exchange.Probes, exchange.Invocation);
        var request = exchange.Probes[0].Request;
        foreach (var unbound in new[]
        {
            request with { Identity = request.Identity with { OwnerId = Guid.NewGuid() } },
            request with { Generation = request.Generation + 1 },
            request with { InputSha256 = new string('b', 64) },
            request with { Ordinal = 2 }
        }) Assert.Throws<IOException>(() => JudgementProbeProtocol.Require(unbound, exchange.Invocation, 1));
        Assert.Throws<IOException>(() => JudgementProbeProtocol.Require(exchange.Probes[1].Reply, request));
        var observed = Assert.Single(exchange.Probes[0].Reply.Result!.Value.GetProperty("processes").EnumerateArray());
        Assert.Equal(Environment.ProcessId, observed.GetProperty("pid").GetInt32());
        Assert.Equal(new[] { "pid", "ppid", "name", "startTimeUtc" }, observed.EnumerateObject().Select(value => value.Name));
        var visible = Assert.Single(exchange.Probes[1].Reply.Result!.Value.GetProperty("windows").EnumerateArray());
        Assert.Equal(window.Title, visible.GetProperty("title").GetString());
        Assert.True(long.TryParse(visible.GetProperty("hwnd").GetString(), out _));
    }

    [Theory(DisplayName = "Real Probe Faults Cannot Become Success And The Next Invocation Has Fresh Observation Limits")]
    [InlineData("javascript", "options")]
    [InlineData("python", "options")]
    [InlineData("javascript", "calls")]
    [InlineData("python", "calls")]
    public async Task ResetsProbeAllowance(string language, string fault)
    {
        var source = language == "javascript" ? """
function nexusJudge(input) {
  if (input.fault === 'options') nexus.probes.processes({unknown:true});
  else for (let index=0;index<(input.fault==='calls'?17:1);index++) nexus.probes.processes({pid:input.pid});
  return {outcome:'succeeded',reason:'explicit return'};
}
""" : """
def nexus_judge(input):
    if input['fault'] == 'options': nexus.probes.processes({'unknown':True})
    else:
        for index in range(17 if input['fault'] == 'calls' else 1): nexus.probes.processes({'pid':input['pid']})
    return {'outcome':'succeeded','reason':'explicit return'}
""";
        await using var fixture = new JudgementWorkerFixture(language, source, timeoutSeconds: 10);
        await fixture.StartAsync();
        var rejected = await fixture.InvokeAsync(1, input: JsonSerializer.Serialize(new { fault, pid = Environment.ProcessId }));
        var final = await fixture.InvokeAsync(2, true, JsonSerializer.Serialize(new { fault = "none", pid = Environment.ProcessId }));
        await fixture.AssertExitedAsync();
        Assert.Null(rejected.Response.Result); Assert.Equal("judgement_worker_execution_failed", rejected.Response.FaultCode);
        Assert.Equal(fault == "calls" ? 16 : 1, rejected.Probes.Count);
        if (fault == "options") Assert.Equal("judgement_probe_options_invalid", rejected.Probes[0].Reply.FaultCode);
        Assert.Null(final.Response.FaultCode); Assert.Equal(BusinessOutcome.Succeeded, final.Response.Result!.Outcome);
        Assert.Single(final.Probes); Assert.Equal(1, final.Probes[0].Request.Ordinal);
        Assert.Equal(2, final.Probes[0].Request.Generation);
    }
}
