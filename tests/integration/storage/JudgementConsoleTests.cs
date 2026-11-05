using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Execution.Judgement;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class JudgementConsoleTests
{
    [Theory(DisplayName = "Real Interpreter Console Diagnostics Belong To Their Own Response And Never Become A Business Result")]
    [InlineData("javascript")]
    [InlineData("python")]
    public async Task SeparatesConsoleDiagnostics(string language)
    {
        var source = language == "javascript" ? """
console.warn('{"status":"success","reason":"console is not a result"}');
function nexusJudge(input) {
  console.info(input.text, 1, true, null);
  if (typeof console.log.GetType !== 'undefined') throw new Error('CLR wrapper exposed');
  return {outcome:'unverified',reason:''};
}
""" : """
import sys
print('{"status":"success","reason":"console is not a result"}', file=sys.stderr)
def nexus_judge(input):
    print(input['text'], 1, True, None)
    return {'outcome':'unverified','reason':''}
""";
        await using var fixture = new JudgementWorkerFixture(language, source);
        await fixture.StartAsync();
        var first = await fixture.InvokeAsync(1, input: JsonSerializer.Serialize(new { text = "first 雪" }));
        var final = await fixture.InvokeAsync(2, true, JsonSerializer.Serialize(new { text = "final 🙂" }));
        await fixture.AssertExitedAsync();
        foreach (var exchange in new[] { first, final })
        {
            Assert.Null(exchange.Response.FaultCode); Assert.Equal(BusinessOutcome.Unverified, exchange.Response.Result!.Outcome);
            Assert.NotEmpty(exchange.Response.Console);
            Assert.True(JudgementWorkerProtocol.Encode(exchange.Response).Length <= JudgementWorkerProtocol.MaximumResponseBytes);
        }
        var firstText = string.Concat(first.Response.Console.Select(entry => entry.Text));
        var finalText = string.Concat(final.Response.Console.Select(entry => entry.Text));
        Assert.Contains("first 雪", firstText); Assert.DoesNotContain("final 🙂", firstText);
        Assert.Contains("final 🙂", finalText); Assert.DoesNotContain("first 雪", finalText);
        Assert.Contains(first.Response.Console, entry => entry.Level == (language == "javascript" ? "warn" : "error"));
    }

    [Theory(DisplayName = "Real Console Overflow Cannot Forge Success And The Next Invocation Has An Independent Allowance")]
    [InlineData("javascript", "bytes")]
    [InlineData("javascript", "entries")]
    [InlineData("python", "bytes")]
    [InlineData("python", "entries")]
    public async Task BoundsConsoleOutput(string language, string limit)
    {
        var source = language == "javascript" ? """
function nexusJudge(input) {
  if (input.overflow) {
    if (input.limit === 'bytes') console.log('雪'.repeat(5462));
    else for (let index = 0; index < 129; index++) console.log('x');
  }
  console.log('fresh call');
  return {outcome:'succeeded',reason:'explicit return'};
}
""" : """
import sys
def nexus_judge(input):
    if input['overflow']:
        if input['limit'] == 'bytes': sys.stdout.write('雪' * 5462)
        else:
            for index in range(129): sys.stdout.write('x')
    print('fresh call')
    return {'outcome':'succeeded','reason':'explicit return'}
""";
        await using var fixture = new JudgementWorkerFixture(language, source);
        await fixture.StartAsync();
        var rejected = await fixture.InvokeAsync(1, input: JsonSerializer.Serialize(new { overflow = true, limit }));
        var final = await fixture.InvokeAsync(2, true, JsonSerializer.Serialize(new { overflow = false, limit }));
        await fixture.AssertExitedAsync();
        Assert.Null(rejected.Response.Result); Assert.Equal("judgement_worker_execution_failed", rejected.Response.FaultCode);
        Assert.True(rejected.Response.Console.Count <= JudgementConsole.MaximumEntries);
        Assert.True(rejected.Response.Console.Sum(entry => JudgementWorkerProtocol.Utf8.GetByteCount(entry.Text)) <= JudgementConsole.MaximumBytes);
        Assert.Null(final.Response.FaultCode); Assert.Equal(BusinessOutcome.Succeeded, final.Response.Result!.Outcome);
        Assert.Equal("fresh call", string.Concat(final.Response.Console.Select(entry => entry.Text)).TrimEnd('\n', '\r'));
    }
}
