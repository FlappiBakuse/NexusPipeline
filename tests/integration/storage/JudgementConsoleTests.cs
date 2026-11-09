using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Execution;
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
        const string printedResult = """{"outcome":"succeeded","reason":"console is not a result"}""";
        var javascript = language == "javascript";
        var source = javascript ? $$"""
console.warn('{{printedResult}}');
function nexusJudge(input) {
  console.info(input.text, 1, true, null);
  if (typeof console.log.GetType !== 'undefined') throw new Error('CLR wrapper exposed');
  return {outcome:'unverified',reason:''};
}
""" : $$"""
import sys
print('{{printedResult}}', file=sys.stderr)
def nexus_judge(input):
    print(input['text'], 1, True, None)
    return {'outcome':'unverified','reason':''}
""";
        await using var fixture = new JudgementWorkerFixture(language, source);
        await fixture.StartAsync();
        const string firstText = "first 雪", finalText = "final 🙂";
        var first = await fixture.InvokeAsync(1, input: JsonSerializer.Serialize(new { text = firstText }));
        var final = await fixture.InvokeAsync(2, true, JsonSerializer.Serialize(new { text = finalText }));
        await fixture.AssertExitedAsync();
        foreach (var (exchange, text) in new[] { (first, firstText), (final, finalText) })
        {
            Assert.Equal(new BusinessJudgementResult(BusinessOutcome.Unverified, ""), exchange.Response.Result);
            var diagnostics = exchange.Response.Console;
            Assert.Equal(printedResult, string.Concat(diagnostics.Where(entry => entry.Level == (javascript ? "warn" : "error"))
                .Select(entry => entry.Text)).TrimEnd('\r', '\n'));
            Assert.Equal(text + (javascript ? " 1 true null" : " 1 True None"),
                string.Concat(diagnostics.Where(entry => entry.Level == (javascript ? "info" : "log")).Select(entry => entry.Text)).TrimEnd('\r', '\n'));
        }
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
  if (input.limit === 'bytes') {
    console.log('雪'.repeat(5461) + 'x');
    if (input.overflow) console.log('x');
  } else for (let index = 0; index < (input.overflow ? 129 : 128); index++) console.log('x');
  return {outcome:'succeeded',reason:'explicit return'};
}
""" : """
import sys
def nexus_judge(input):
    if input['limit'] == 'bytes':
        sys.stdout.write('雪' * 5461 + 'x')
        if input['overflow']: sys.stdout.write('x')
    else:
        for index in range(129 if input['overflow'] else 128): sys.stdout.write('x')
    return {'outcome':'succeeded','reason':'explicit return'}
""";
        await using var fixture = new JudgementWorkerFixture(language, source);
        await fixture.StartAsync();
        var rejected = await fixture.InvokeAsync(1, input: JsonSerializer.Serialize(new { overflow = true, limit }));
        var final = await fixture.InvokeAsync(2, true, JsonSerializer.Serialize(new { overflow = false, limit }));
        await fixture.AssertExitedAsync();
        Assert.Null(rejected.Response.Result); Assert.Equal("judgement_worker_execution_failed", rejected.Response.FaultCode);
        Assert.Equal(new BusinessJudgementResult(BusinessOutcome.Succeeded, "explicit return"), final.Response.Result);
        Assert.Equal(limit == "bytes" ? new string('雪', 5461) + "x" : new string('x', 128),
            string.Concat(final.Response.Console.Select(entry => entry.Text)));
    }
}
