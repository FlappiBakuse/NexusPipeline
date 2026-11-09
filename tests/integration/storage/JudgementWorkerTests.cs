using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Execution.Judgement;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class JudgementWorkerTests
{
    [Fact(DisplayName = "Actual Javascript Worker Creates A Fresh Engine For Each Invocation And Closes After Its Only Final Result")]
    public async Task IsolatesJavascriptInvocations()
    {
        await using var fixture = new JudgementWorkerFixture("javascript", """
globalThis.calls = (globalThis.calls || 0) + 1;
globalThis.nexusJudge = input => ({ outcome: calls === 1 && Array.isArray(input.values) && input.values[0] === null &&
  input.values[1] === true && input.values[2] === 1.25 && input.values[3].text === '雪\n' &&
  Object.hasOwn(input, '__proto__') && input.__proto__.local === 1 && !('local' in input) && !('local' in {})
  ? 'succeeded' : 'failed', reason: input.reason });
""");
        await fixture.StartAsync();
        const string shape = ",\"values\":[null,true,1.25,{\"text\":\"雪\\n\"}],\"__proto__\":{\"local\":1}}";
        var first = await fixture.InvokeAsync(1, input: "{\"reason\":\"first\"" + shape);
        var final = await fixture.InvokeAsync(2, true, "{\"reason\":\"last\"" + shape);
        Assert.Equal(new BusinessJudgementResult(BusinessOutcome.Succeeded, "first"), first.Response.Result);
        Assert.Equal(new BusinessJudgementResult(BusinessOutcome.Succeeded, "last"), final.Response.Result);
        Assert.Empty(first.InterpreterChildren); Assert.Empty(final.InterpreterChildren);
        await fixture.AssertExitedAsync();
        await Assert.ThrowsAsync<IOException>(() => fixture.Client.InvokeAsync(fixture.Invocation(3), CancellationToken.None));
    }

    [Fact(DisplayName = "Actual Python Worker Uses Different Admitted Native Interpreters And Does Not Keep Sys Modules Between Calls")]
    public async Task IsolatesPythonInterpreters()
    {
        await using var fixture = new JudgementWorkerFixture("python", """
import sys
import types
def nexus_judge(value):
    old = 'nexus_test_shared_state' in sys.modules
    sys.modules['nexus_test_shared_state'] = types.ModuleType('nexus_test_shared_state')
    print('user text is outside the response frame')
    return {'outcome': 'failed' if old else 'partial', 'reason': value['reason']}
""");
        await fixture.StartAsync();
        var first = await fixture.InvokeAsync(1, input: "{\"reason\":\"one\"}");
        var second = await fixture.InvokeAsync(2, true, "{\"reason\":\"two\"}");
        Assert.Equal(new BusinessJudgementResult(BusinessOutcome.Partial, "one"), first.Response.Result);
        Assert.Equal(new BusinessJudgementResult(BusinessOutcome.Partial, "two"), second.Response.Result);
        var firstChild = Assert.Single(first.InterpreterChildren);
        var secondChild = Assert.Single(second.InterpreterChildren);
        Assert.NotEqual((firstChild.ProcessId, firstChild.CreatedAtUtc), (secondChild.ProcessId, secondChild.CreatedAtUtc));
        Assert.Equal(new[] { firstChild, secondChild }, fixture.AdmittedChildren);
        await fixture.AssertExitedAsync();
    }

    [Theory(DisplayName = "Actual Interpreters Receive A Complete Large Attempt Log")]
    [InlineData("javascript")]
    [InlineData("python")]
    public async Task BoundsLargeInputs(string language)
    {
        var source = language == "javascript" ? "globalThis.nexusJudge = input => ({outcome:input.log.length === 3145728 && input.log.startsWith('始') && input.log.endsWith('雪') ? 'succeeded' : 'failed',reason:'large actual input'});" :
            "def nexus_judge(input):\n log = input['log']\n return {'outcome': 'succeeded' if len(log) == 3145728 and log.startswith('始') and log.endswith('雪') else 'failed', 'reason': 'large actual input'}";
        await using var fixture = new JudgementWorkerFixture(language, source, timeoutSeconds: 10);
        await fixture.StartAsync();
        var input = JsonSerializer.Serialize(new { log = "始" + new string('x', 3 * 1024 * 1024 - 2) + "雪" });
        var exchange = await fixture.InvokeAsync(1, true, input);
        Assert.Equal(BusinessOutcome.Succeeded, Assert.IsType<BusinessJudgementResult>(exchange.Response.Result).Outcome);
        await fixture.AssertExitedAsync();
    }

    [Theory(DisplayName = "Actual Interpreter Errors And Invalid Business Outcomes Produce Bounded Faults Without Inventing Success")]
    [InlineData("javascript", "globalThis.nexusJudge = () => ({outcome:'cancelled',reason:'private secret'});")]
    [InlineData("javascript", "globalThis.nexusJudge = () => { throw new Error('private secret'); };")]
    [InlineData("python", "def nexus_judge(value):\n    raise ValueError('private secret')")]
    public async Task ReportsBoundedFaults(string language, string source)
    {
        await using var fixture = new JudgementWorkerFixture(language, source);
        await fixture.StartAsync();
        var exchange = await fixture.InvokeAsync(1, true);
        Assert.Null(exchange.Response.Result);
        Assert.Equal("judgement_worker_execution_failed", exchange.Response.FaultCode);
        Assert.DoesNotContain("private secret", Encoding.UTF8.GetString(JudgementWorkerProtocol.Encode(exchange.Response)));
        await fixture.AssertExitedAsync();
    }

    [Theory(DisplayName = "Actual Nonreturning Interpreters Are Bounded And Their Original Native Families Are Stopped")]
    [InlineData("javascript", "globalThis.nexusJudge = () => { while (true) {} };")]
    [InlineData("python", "def nexus_judge(value):\n    while True: pass")]
    public async Task StopsNonreturningWorkers(string language, string source)
    {
        await using var fixture = new JudgementWorkerFixture(language, source, timeoutSeconds: 1);
        await fixture.StartAsync();
        using var outerDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(11));
        JudgementWorkerExchange? exchange = null;
        var error = await Record.ExceptionAsync(async () =>
            exchange = await fixture.Client.InvokeAsync(fixture.Invocation(1, true), outerDeadline.Token));
        Assert.False(outerDeadline.IsCancellationRequested);
        if (error is OperationCanceledException) return;
        Assert.Null(error);
        Assert.NotNull(exchange);
        Assert.Null(exchange.Response.Result);
        Assert.Contains(exchange.Response.FaultCode, new[] { "judgement_worker_timeout", "judgement_worker_execution_failed" });
        await fixture.AssertExitedAsync();
    }

    [Fact(DisplayName = "The Parent Rejects An Unbound Owner Before Admitting The Invocation")]
    public async Task RejectsForeignOwnerBeforeAdmission()
    {
        await using var fixture = new JudgementWorkerFixture("javascript", "globalThis.nexusJudge = () => ({outcome:'unverified',reason:''});",
            admitInvocation: (_, _) => throw new InvalidOperationException("unbound_invocation_admitted"));
        await fixture.StartAsync();
        var invocation = fixture.Invocation(1);
        invocation = invocation with { Identity = invocation.Identity with { OwnerId = Guid.NewGuid() } };
        await Assert.ThrowsAsync<JsonException>(() => fixture.Client.InvokeAsync(invocation, CancellationToken.None));
    }

    [Theory(DisplayName = "The Actual Broker Also Rejects Wrong Wire Identity Without Returning A Business Response")]
    [InlineData("generation")]
    [InlineData("owner")]
    [InlineData("hash")]
    public async Task RejectsWrongIdentity(string field)
    {
        await using var fixture = new JudgementWorkerFixture("javascript", "globalThis.nexusJudge = () => ({outcome:'succeeded',reason:'must not run'});");
        await fixture.StartAsync();
        var invocation = fixture.Invocation(1);
        invocation = field switch
        {
            "generation" => invocation with { Generation = 2 },
            "owner" => invocation with { Identity = invocation.Identity with { OwnerId = Guid.NewGuid() } },
            _ => invocation with { InputSha256 = new string('f', 64) }
        };
        await fixture.AssertFrameAsync(invocation);
    }

    [Theory(DisplayName = "Worker Frames Reject Negative Empty Oversized And Incomplete Bodies Before Deserialization")]
    [InlineData(-1, 0, "size_invalid")]
    [InlineData(0, 0, "size_invalid")]
    [InlineData(2097153, 0, "size_invalid")]
    [InlineData(4, 2, "incomplete")]
    public async Task RejectsInvalidFrames(int size, int bodyBytes, string fault)
    {
        var bytes = new byte[4 + bodyBytes]; BinaryPrimitives.WriteInt32LittleEndian(bytes, size);
        using var stream = new MemoryStream(bytes);
        var error = await Assert.ThrowsAsync<IOException>(() => JudgementWorkerFraming.ReadAsync(stream, CancellationToken.None));
        Assert.Equal("judgement_worker_frame_" + fault, error.Message);
    }
}
