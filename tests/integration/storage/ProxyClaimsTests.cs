using NexusPipeline.Platform.Storage;
using System.Runtime.Versioning;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Core.Settings;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.TestFixtures;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Secrets;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ProxyClaimsTests
{
    [Theory(DisplayName = "Formal Start Rejects An Unreadable Configured Proxy Password Before Any Execution Acceptance")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsUnreadableCredentials(bool missing)
    {
        using var fixture = Runtime();
        var source = fixture.Source; SetWorker(source);
        await using var proxy = new NativeHttpFixture(_ => new(200, "must not be requested"u8.ToArray()));
        var settings = source.Settings.Read();
        var change = new HostSettingsChange(settings.Revision, settings.Values with
            { Proxy = new(ProxyMode.Http, proxy.Address.ToString(), "fixture-user") });
        source.Settings.Commit(HostSettingsStore.Identity(Guid.NewGuid(), "unreadable-proxy", change), change, DateTimeOffset.UtcNow);
        var secrets = Secrets(fixture); var key = SecretFieldKey.HostProxyPassword(source.Instance.InstanceId);
        using var value = new SecretValue("unreadable-formal-proxy-fixture-canary");
        var status = await secrets.ApplyAsync(Guid.NewGuid(), key, 0, SecretDisposition.Set, value, default);
        var payload = Assert.Single(Directory.GetFiles(Path.Combine(source.Instance.RuntimeRoot, "secrets/payloads")));
        if (missing) File.Delete(payload);
        else { var bytes = File.ReadAllBytes(payload); bytes[0] ^= 1; File.WriteAllBytes(payload, bytes); }
        var before = source.Snapshot();
        var operation = Guid.NewGuid(); var request = source.Request(new BindingTarget(source.OtherBinding));
        Assert.Equal("secret_unavailable", Assert.Throws<IOException>(() =>
            fixture.Service.Start(operation, "unreadable-proxy", request, DateTimeOffset.UtcNow)).Message);
        Assert.Equal(before.Rows, source.Snapshot().Rows);
        Assert.Null(new RunAttemptAcceptance(source.Instance, new(), source.Database)
            .LookupStart(ExecutionStartIdentity.Create(operation, "unreadable-proxy", request)));
        var after = await secrets.GetStateAsync(key, default);
        Assert.Equal(status.SecretId, after.SecretId); Assert.Equal(status.Revision, after.Revision);
        Assert.True(after.IsConfigured); Assert.False(after.IsReadable); Assert.Empty(proxy.Requests);
    }

    [Fact(DisplayName = "Formal Start Freezes The Actual Proxy And Opaque Credential Under Its Shared Root")]
    public async Task FreezesProxyClaims()
    {
        using var fixture = Runtime();
        var source = fixture.Source;
        SetWorker(source);
        var settings = new HostSettingsStore(source.Database);
        var current = settings.Read();
        var proxy = new ProxyPreferences(ProxyMode.Http, "http://127.0.0.1:58739", "fixture-proxy-user");
        var change = new HostSettingsChange(current.Revision, current.Values with { Proxy = proxy });
        settings.Commit(HostSettingsStore.Identity(Guid.NewGuid(), "proxy-admission", change), change, DateTimeOffset.UtcNow);
        var secrets = Secrets(fixture);
        using var material = new SecretValue("formal-credential-fixture-canary");
        var state = await secrets.ApplyAsync(Guid.NewGuid(), SecretFieldKey.HostProxyPassword(source.Instance.InstanceId),
            0, SecretDisposition.Set, material, default);
        var operation = Guid.NewGuid();
        var receipt = fixture.Service.Start(operation, "proxy-admission", source.Request(new BindingTarget(source.OtherBinding)), DateTimeOffset.UtcNow);
        using var accepted = fixture.Take(operation);
        var plan = accepted.Prepared.Total.Plan.ReadWire();
        var network = Assert.IsType<PreparedExecutionNetwork>(accepted.Prepared.Network);
        Assert.Equal(settings.Read().Revision, plan.SettingsRevision);
        Assert.Equal(proxy, network.Proxy);
        Assert.Equal(new SecretFieldReference(state.Revision, state.SecretId), network.Credential.Reference);
        network.Credential.Use(value => Assert.True(value.SequenceEqual("formal-credential-fixture-canary")));
        Assert.Throws<InvalidOperationException>(() => secrets.ApplyAsync(Guid.NewGuid(),
            SecretFieldKey.HostProxyPassword(source.Instance.InstanceId), state.Revision, SecretDisposition.Clear, null, default).GetAwaiter().GetResult());
        using var mutation = fixture.Resources.TryAcquire(Guid.NewGuid(), [new("settings:host", ResourceClaimMode.Mutation)]).Lease;
        Assert.Null(mutation);
        var persisted = new ExecutionPlanCodec(source.Instance, new(), new(source.Instance, new())).Read(accepted.Prepared.Total.Reference).ReadWire();
        Assert.Equal(state.SecretId?.ToString("D"), persisted.JudgementNetwork!.PasswordSecretId);
        Assert.Equal(network.Proxy, JudgementNetworkBinding.Proxy(persisted.JudgementNetwork));
        Assert.True(receipt.Acceptance.IsNew);
    }

    [Fact(DisplayName = "A Changed Credential After Preview Rejects Preparation Before Any Execution Operation")]
    public async Task RejectsChangedCredentials()
    {
        using var fixture = Runtime();
        var source = fixture.Source; SetWorker(source);
        var builder = Builder(source);
        using var preview = builder.Prepare(source.Request(new BindingTarget(source.OtherBinding)), DateTimeOffset.UtcNow);
        var secrets = Secrets(fixture);
        using var material = new SecretValue("changed-after-preview-fixture");
        await secrets.ApplyAsync(Guid.NewGuid(), SecretFieldKey.HostProxyPassword(source.Instance.InstanceId),
            0, SecretDisposition.Set, material, default);
        var operation = Guid.NewGuid(); var owner = new object();
        using var draft = new ExecutionAdmissionDraft(owner, preview, TimeProvider.System, TimeProvider.System.GetTimestamp());
        using var root = fixture.Resources.TryAcquire(operation, ExecutionPlanResources.Claims(preview.ReadPlan()!.ReadWire())).Lease!;
        var identity = ExecutionStartIdentity.Create(operation, "proxy-admission", source.Request(new BindingTarget(source.OtherBinding)));
        var before = source.Snapshot();
        var error = Assert.Throws<InvalidOperationException>(() => new ExecutionAdmissionPreparation(owner, source.Instance, source.Database,
            new(), source.Lease, fixture.Resources, fixture.Index, builder,
            new(PhysicalPathPolicy.RuntimeApplicationDirectory(), Path.Combine(source.Instance.RuntimeRoot, "extensions"))).Freeze(identity, draft, root));
        Assert.Equal("secret_revision_conflict", error.Message);
        Assert.Equal(before.Rows, source.Snapshot().Rows);
        Assert.Null(new RunAttemptAcceptance(source.Instance, new(), source.Database).LookupStart(identity));
    }

    [Fact(DisplayName = "Acceptance Rechecks The Exact Credential Reference Inside The Same Transaction")]
    public void RechecksCredentialTransaction()
    {
        using var fixture = Runtime();
        var source = fixture.Source; SetWorker(source);
        var builder = Builder(source); var request = source.Request(new BindingTarget(source.OtherBinding));
        using var preview = builder.Prepare(request, DateTimeOffset.UtcNow);
        var operation = Guid.NewGuid(); var owner = new object();
        var identity = ExecutionStartIdentity.Create(operation, "proxy-admission", request);
        using var draft = new ExecutionAdmissionDraft(owner, preview, TimeProvider.System, TimeProvider.System.GetTimestamp());
        using var root = fixture.Resources.TryAcquire(operation, ExecutionPlanResources.Claims(preview.ReadPlan()!.ReadWire())).Lease!;
        using var prepared = new ExecutionAdmissionPreparation(owner, source.Instance, source.Database, new(), source.Lease,
            fixture.Resources, fixture.Index, builder,
            new(PhysicalPathPolicy.RuntimeApplicationDirectory(), Path.Combine(source.Instance.RuntimeRoot, "extensions"))).Freeze(identity, draft, root);
        fixture.Execute("INSERT INTO system.secret_fields(owner_kind,owner_id,subject_id,field,revision,secret_id) " +
            $"VALUES('host','{source.Instance.InstanceId:D}','{source.Instance.InstanceId:D}','proxy-password',1,NULL);");
        var before = source.Snapshot();
        var acceptance = new RunAttemptAcceptance(source.Instance, new(), source.Database);
        Assert.Equal("secret_revision_conflict", Assert.Throws<InvalidOperationException>(() =>
            acceptance.AcceptStart(identity, preview, prepared.Attempt, DateTimeOffset.UtcNow)).Message);
        Assert.Null(acceptance.LookupStart(identity));
        Assert.Equal(before.Rows, source.Snapshot().Rows);
    }

    private static ExecutionAdmissionFixture Runtime() => new(judgementRuntime: new(
        Environment.GetEnvironmentVariable("NEXUS_TEST_HOST_EXE")!, Environment.GetEnvironmentVariable("NEXUS_TEST_PYTHON_EXE")));
    private static SecretFieldService Secrets(ExecutionAdmissionFixture fixture) => new(fixture.Source.Instance, fixture.Source.Database,
        fixture.Source.Lease, fixture.Resources, fixture.Index, new());
    private static ExecutionPlanBuilder Builder(PlanBuilderFixture source) => new(source.Instance, source.Database, new(), source.Lease,
        new(5, 6, 7), new(Environment.GetEnvironmentVariable("NEXUS_TEST_HOST_EXE")!, Environment.GetEnvironmentVariable("NEXUS_TEST_PYTHON_EXE")));
    private static void SetWorker(PlanBuilderFixture source)
    {
        var catalog = source.Definitions.Read();
        var changed = catalog.Scripts.Single(script => script.Id == source.OtherScriptId) with
            { Judgement = new(AutomationJudgementLanguage.JavaScript, [], [], "globalThis.nexusJudge=()=>({outcome:'unverified',reason:''});") };
        var change = new AutomationChanges(catalog.Revision, [], [changed], [], [], [], [], []);
        source.Definitions.Commit(NexusPipeline.Platform.Storage.Automation.AutomationDefinitionStore.Identity(
            Guid.NewGuid(), "proxy-admission", change), change, DateTimeOffset.UtcNow);
    }

    [Theory(DisplayName = "Actual Frozen Worker Claims Reserve The Host Credential Before Any Execution Acceptance")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReservesWorkerCredentials(bool scriptWorker)
    {
        using var source = new PlanBuilderFixture();
        if (scriptWorker)
        {
            var catalog = source.Definitions.Read();
            var changed = catalog.Scripts.Single(script => script.Id == source.OtherScriptId) with
                { Judgement = new(AutomationJudgementLanguage.JavaScript, [], [], "globalThis.nexusJudge = () => ({outcome:'unverified',reason:''});") };
            var change = new AutomationChanges(catalog.Revision, [], [changed], [], [], [], [], []);
            source.Definitions.Commit(NexusPipeline.Platform.Storage.Automation.AutomationDefinitionStore.Identity(
                Guid.NewGuid(), "host-proxy-claim-fixture", change), change, PlanBuilderFixture.CreatedAt);
        }
        var index = new StartupJournalRecovery(source.Instance, new(), source.Database,
            new(Path.Combine(source.Root, "host"), Path.Combine(source.Instance.RuntimeRoot, "extensions"))).Recover(source.Lease);
        var resources = new ResourceLeaseManager(new(index, TimeProvider.System));
        var secrets = new SecretFieldService(source.Instance, source.Database, source.Lease, resources, index, new());
        using var value = new SecretValue("actual-frozen-claim-dpapi-value");
        var status = await secrets.ApplyAsync(Guid.NewGuid(), SecretFieldKey.HostProxyPassword(source.Instance.InstanceId),
            0, SecretDisposition.Set, value, default);
        var before = source.Snapshot();
        var builder = new ExecutionPlanBuilder(source.Instance, source.Database, new(), source.Lease, new(5, 6, 7),
            new(Environment.GetEnvironmentVariable("NEXUS_TEST_HOST_EXE")!, Environment.GetEnvironmentVariable("NEXUS_TEST_PYTHON_EXE")));
        using var draft = builder.Prepare(source.Request(new BindingTarget(source.OtherBinding)), PlanBuilderFixture.CreatedAt);
        var plan = Assert.IsType<FrozenExecutionPlan>(draft.ReadPlan()).ReadWire();
        var claims = ExecutionPlanResources.Claims(plan);
        Assert.Equal(scriptWorker, claims.Any(claim => claim.ResourceKey == "secrets:fields"));
        var operation = Guid.NewGuid();
        using var root = resources.TryAcquire(operation, claims).Lease ?? throw new IOException("fixture_execution_root_unavailable");
        if (scriptWorker)
        {
            Assert.Equal(ResourceClaimMode.Reference, Assert.Single(claims, claim => claim.ResourceKey == "secrets:fields").Mode);
            using var credential = secrets.PrepareProxyCredential(root, operation);
            Assert.Equal(new SecretFieldReference(status.Revision, status.SecretId), credential.Reference);
            credential.Use(secret => Assert.True(secret.SequenceEqual("actual-frozen-claim-dpapi-value")));
            using var mutation = resources.TryAcquire(Guid.NewGuid(), [new("secrets:fields", ResourceClaimMode.Mutation)]).Lease;
            Assert.Null(mutation);
        }
        else Assert.Throws<InvalidOperationException>(() => secrets.PrepareProxyCredential(root, operation));
        Assert.Equal(before.Rows, source.Snapshot().Rows);
        Assert.Equal(before.Files, source.Snapshot().Files);
    }
}
