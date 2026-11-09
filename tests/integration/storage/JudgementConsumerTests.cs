using System.Runtime.Versioning;
using System.Text;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.TestFixtures;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class JudgementConsumerTests
{
    [Fact(DisplayName = "The Formal Consumer Uses Only Its Opaque Dpapi Proxy Password And History Survives Its Retirement")]
    public async Task ProtectsRetiredCredentials()
    {
        const string canary = "formal-proxy-dpapi-credential-canary-018", user = "formal-proxy-user";
        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + canary));
        await using var proxy = new NativeHttpFixture(request =>
            request.Headers.GetValueOrDefault("Proxy-Authorization") == expected
                ? new(200, "authenticated"u8.ToArray())
                : new(407, [], new Dictionary<string, string> { ["Proxy-Authenticate"] = "Basic realm=fixture" }));
        var script = "function nexusJudge(input) { if (!input.isFinal) return {outcome:'unverified',reason:''}; " +
            "const r=nexus.probes.httpGet({url:'http://nexus-formal-proxy.invalid/body'}); " +
            "return {outcome:r.ok&&r.body==='authenticated'?'succeeded':'failed',reason:'actual authenticated proxy'}; }";
        SecretFieldStatus? configured = null;
        using var fixture = new JudgementIntegrationFixture(AutomationJudgementLanguage.JavaScript, script, beforeAccept: runtime =>
        {
            var settings = runtime.Source.Settings.Read();
            var change = new HostSettingsChange(settings.Revision, settings.Values with
                { Proxy = new(ProxyMode.Http, proxy.Address.ToString(), user) });
            runtime.Source.Settings.Commit(HostSettingsStore.Identity(Guid.NewGuid(), "formal-http-proxy", change), change, DateTimeOffset.UtcNow);
            using var value = new SecretValue(canary);
            configured = Secrets(runtime).ApplyAsync(Guid.NewGuid(), SecretFieldKey.HostProxyPassword(runtime.Source.Instance.InstanceId),
                0, SecretDisposition.Set, value, default).GetAwaiter().GetResult();
        });
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle); Assert.Equal(BusinessOutcome.Succeeded, receipt.Facts.Business);
        var state = fixture.State;
        Assert.Equal(new SecretFieldReference(configured!.Revision, configured.SecretId),
            JudgementNetworkBinding.Reference(state.Total.JudgementNetwork!));
        Assert.Contains(proxy.Requests, request => request.Headers.GetValueOrDefault("Proxy-Authorization") == expected);
        var requestCount = proxy.Requests.Count;
        var cleared = await Secrets(fixture.Base).ApplyAsync(Guid.NewGuid(), SecretFieldKey.HostProxyPassword(fixture.Source.Instance.InstanceId),
            configured.Revision, SecretDisposition.Clear, null, default);
        Assert.False(cleared.IsConfigured);
        var result = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total));
        Assert.Equal(BusinessOutcome.Succeeded, new StepEvidenceReader(fixture.Source.Instance, fixture.Source.Database, new())
            .Read(result.Binding, result.Latest.Artifact, true).Business);
        Assert.Equal(requestCount, proxy.Requests.Count);
        var credentialForms = new[] { Encoding.UTF8.GetBytes(canary), Encoding.Unicode.GetBytes(canary), Encoding.ASCII.GetBytes(expected) };
        foreach (var path in Directory.EnumerateFiles(fixture.Source.Instance.RuntimeRoot, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) != "instance.lock" && !path.EndsWith("-shm", StringComparison.Ordinal)))
        {
            var bytes = File.ReadAllBytes(path);
            Assert.All(credentialForms, form => Assert.True(bytes.AsSpan().IndexOf(form) < 0));
        }
    }

    [Fact(DisplayName = "Accepted Cancellation Aborts The Formal Consumers Actual Pending Http Request And Keeps Business Unverified")]
    public async Task CancelsPendingHttp()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new NativeHttpFixture(async (_, stopping) =>
        {
            entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, stopping); return new(200, []);
        });
        var script = "function nexusJudge(input) { nexus.probes.httpGet({url:'" + server.Address +
            "',timeoutMs:15000}); return {outcome:'succeeded',reason:'late observation must not win'}; }";
        using var fixture = new JudgementIntegrationFixture(AutomationJudgementLanguage.JavaScript, script, mode: "wait");
        var running = fixture.Consumer.RunNextAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        fixture.Cancel();
        var receipt = Assert.IsType<AttemptReceipt>(await running.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(LifecycleOutcome.Cancelled, receipt.Facts.Lifecycle); Assert.Equal(BusinessOutcome.Unverified, receipt.Facts.Business);
        Assert.Equal(0, fixture.Consumer.PendingReconciliationCount); Assert.Single(server.Requests);
        Assert.Equal(JournalPhase.Committed, fixture.State.Journal.Phase);
    }

    private static SecretFieldService Secrets(ExecutionAdmissionFixture fixture) => new(fixture.Source.Instance, fixture.Source.Database,
        fixture.Source.Lease, fixture.Resources, fixture.Index, new());

    [Theory(DisplayName = "The Formal Consumer Uses Its Frozen Network For A Real Http Body And Replays Only Saved Observations")]
    [InlineData(AutomationJudgementLanguage.JavaScript)]
    [InlineData(AutomationJudgementLanguage.Python)]
    public async Task ReplaysFrozenNetwork(AutomationJudgementLanguage language)
    {
        await using var server = new NativeHttpFixture(_ => new(200, Enumerable.Repeat((byte)'<', 2 * 1024 * 1024).ToArray()));
        var url = server.Address.ToString();
        var script = language == AutomationJudgementLanguage.JavaScript ? $$"""
function nexusJudge(input) {
  if (!input.isFinal) return {outcome:'unverified',reason:''};
  const observed = nexus.probes.httpGet({url:'{{url}}',maxBytes:2097152});
  if (!observed.ok || observed.status !== 200 || observed.body.length !== 2097152 || observed.body[0] !== '<' || observed.truncated)
    throw new Error('native HTTP mismatch');
  return {outcome:'succeeded',reason:'actual frozen HTTP'};
}
""" : $$"""
def nexus_judge(input):
    if not input['isFinal']: return {'outcome':'unverified','reason':''}
    observed = nexus.probes.httpGet({'url':'{{url}}','maxBytes':2097152})
    assert observed['ok'] and observed['status'] == 200 and len(observed['body']) == 2097152
    assert observed['body'][0] == '<' and not observed['truncated']
    return {'outcome':'succeeded','reason':'actual frozen HTTP'}
""";
        using var fixture = new JudgementIntegrationFixture(language, script);
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
        Assert.Equal(EngineOutcome.Failed, receipt.Facts.Engine); Assert.Equal(BusinessOutcome.Succeeded, receipt.Facts.Business);
        var state = fixture.State;
        var result = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total));
        var reader = new StepEvidenceReader(fixture.Source.Instance, fixture.Source.Database, new());
        var blobs = new ImmutableBlobStore(fixture.Source.Instance, new());
        var evidence = StepEvidenceCodec.Read(blobs, result.Binding, result.Latest.Artifact);
        Assert.Equal(37, evidence.ExitCode);
        var judgement = JudgementEvidenceCodec.Read(blobs, result.Binding, evidence.Judgement!);
        var probe = Assert.Single(judgement.Exchanges.SelectMany(exchange => exchange.ProbeObservations));
        var body = JudgementProbeArchive.Decode(blobs, probe).Reply.Result!.Value.GetProperty("body").GetString()!;
        Assert.Equal(2 * 1024 * 1024, body.Length);
        Assert.True(body.AsSpan().IndexOfAnyExcept('<') < 0);
        Assert.Equal(BusinessOutcome.Succeeded, reader.Read(result.Binding, result.Latest.Artifact, true).Business);
        Assert.Single(server.Requests);
    }
}
