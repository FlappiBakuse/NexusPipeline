using System.Runtime.Versioning;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Execution.Judgement;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ProviderInputsTests
{
    [Fact(DisplayName = "A Controlled Activation Produces An Opaque Dry Run With Actual Worker Upstream And Package Identity Without Any Writes")]
    public async Task PreviewsOpaqueProvider()
    {
        using var source = new PlanBuilderFixture(otherExtensionId: "provider-fixture");
        using var packageFixture = new ProviderPackageFixture();
        MakeSpecialized(source);
        var registry = new ExecutionProviderRegistry(); await using var scope = new ExtensionScope("provider-fixture");
        await new ExecutionRegistrationPort(scope, packageFixture.Verify(), registry).RegisterAsync(default); scope.Publish();
        var builder = Builder(source, registry); var before = source.Snapshot(); var packageBefore = packageFixture.Fingerprint();
        using var draft = builder.Prepare(source.Request(new BindingTarget(source.OtherBinding)), PlanBuilderFixture.CreatedAt);
        var plan = Assert.IsType<FrozenExecutionPlan>(draft.ReadPlan()).ReadWire(); var step = Assert.Single(plan.Steps);
        Assert.Equal("extensionWorker", step.EngineKind); Assert.Equal("provider-fixture", step.ExtensionId); Assert.Equal("main", step.EntryId);
        Assert.Equal(packageFixture.Worker, step.ExecutablePath);
        Assert.Equal(source.OtherImage, step.Provider!.Upstream.Path);
        Assert.Equal(scope.ActivationId.ToString("D"), step.Provider.ActivationId);
        Assert.Equal(step.Provider.PackageArchive.Sha256, Assert.Single(plan.ExtensionLocks).PackageSha256);
        Assert.Equal(before.Rows, source.Snapshot().Rows); Assert.Equal(before.Files, source.Snapshot().Files);
        Assert.Equal(packageBefore, packageFixture.Fingerprint());
    }

    [Fact(DisplayName = "A Revoked Activation After Preview Cannot Freeze Or Persist The Former Plan")]
    public async Task RejectsRevokedActivation()
    {
        using var source = new PlanBuilderFixture(otherExtensionId: "provider-fixture"); MakeSpecialized(source);
        using var packageFixture = new ProviderPackageFixture();
        var registry = new ExecutionProviderRegistry(); await using var scope = new ExtensionScope("provider-fixture");
        await new ExecutionRegistrationPort(scope, packageFixture.Verify(), registry).RegisterAsync(default); scope.Publish();
        var builder = Builder(source, registry);
        using var draft = builder.Prepare(source.Request(new BindingTarget(source.OtherBinding)), PlanBuilderFixture.CreatedAt);
        Assert.NotNull(draft.ReadPlan()); await scope.DisposeAsync(); var before = source.Snapshot();
        Assert.Throws<IOException>(() => builder.Freeze(draft));
        Assert.Equal(before.Rows, source.Snapshot().Rows); Assert.Equal(before.Files, source.Snapshot().Files);
    }

    [Theory(DisplayName = "Historical Provider Plan Reads Only Saved Manifest And Metadata After Activation And External Images Change")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadsFrozenMetadata(bool deleteImage)
    {
        using var source = new PlanBuilderFixture(otherExtensionId: "provider-fixture"); MakeSpecialized(source);
        using var packageFixture = new ProviderPackageFixture();
        var registry = new ExecutionProviderRegistry(); await using var scope = new ExtensionScope("provider-fixture");
        await new ExecutionRegistrationPort(scope, packageFixture.Verify(), registry).RegisterAsync(default); scope.Publish();
        var builder = Builder(source, registry);
        using var draft = builder.Prepare(source.Request(new BindingTarget(source.OtherBinding)), PlanBuilderFixture.CreatedAt);
        BlobReference reference;
        using (var prepared = builder.Freeze(draft)) reference = prepared.Reference;
        await scope.DisposeAsync();
        if (deleteImage) File.Delete(packageFixture.Worker); else File.AppendAllText(packageFixture.Worker, "changed");
        var historical = new ExecutionPlanCodec(source.Instance, new(), new(source.Instance, new())).Read(reference).ReadWire();
        Assert.Equal(packageFixture.Worker, Assert.Single(historical.Steps).Provider!.Worker.Path);
        var before = source.Snapshot();
        Assert.Throws<IOException>(() => builder.Freeze(draft));
        Assert.Equal(before.Rows, source.Snapshot().Rows); Assert.Equal(before.Files, source.Snapshot().Files);
    }

    [Fact(DisplayName = "An Unavailable Provider Never Borrows The Generic Saved Configuration Identity Or Creates A Plan")]
    public void RejectsUnavailableProviders()
    {
        using var source = new PlanBuilderFixture(specialized: true);
        var before = source.Snapshot();
        using var draft = source.Builder.Prepare(source.Request(new BindingTarget(source.BindingA)), PlanBuilderFixture.CreatedAt);
        Assert.Null(draft.ReadPlan());
        Assert.Contains(draft.Explanation.Items, item => item.ReasonCode == "execution_extension_provider_unavailable");
        Assert.Equal(before.Rows, source.Snapshot().Rows); Assert.Equal(before.Files, source.Snapshot().Files);
    }

    [Fact(DisplayName = "Three Real Queue Bindings Use The Same Controlled Provider Manifest In Preview Freeze And Historical Read")]
    public async Task UsesSharedProviderManifest()
    {
        using var source = new PlanBuilderFixture(otherExtensionId: "provider-fixture");
        using var package = new ProviderPackageFixture();
        MakeSpecialized(source);
        var catalog = source.Definitions.Read();
        var first = Guid.NewGuid();
        var last = Guid.NewGuid();
        var changes = new AutomationChanges(catalog.Revision, [], [], [],
            [new(first, 0, source.UserA, source.OtherScriptId, true, 0), new(last, 0, source.UserC, source.OtherScriptId, true, 0)],
            [catalog.Queues.Single() with { ScriptIds = [source.OtherScriptId] }], [], []);
        source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "provider-shared-manifest", changes),
            changes, PlanBuilderFixture.CreatedAt);
        var registry = new ExecutionProviderRegistry();
        await using var scope = new ExtensionScope("provider-fixture");
        await new ExecutionRegistrationPort(scope, package.Verify(), registry).RegisterAsync(default);
        scope.Publish();
        var builder = Builder(source, registry);
        var before = source.Snapshot();
        using var draft = builder.Prepare(source.Request(new QueueTarget(source.QueueId)), PlanBuilderFixture.CreatedAt);
        var preview = Assert.IsType<FrozenExecutionPlan>(draft.ReadPlan()).ReadWire();
        Assert.Equal(3, preview.Steps.Count);
        Assert.All(preview.Steps, step => Assert.NotNull(step.Provider));
        var previewProviders = preview.Steps.Select(step => step.Provider!).ToArray();
        Assert.All(previewProviders, provider => Assert.Equal(scope.ActivationId.ToString("D"), provider.ActivationId));
        Assert.Single(previewProviders.Select(provider => provider.Manifest).Distinct());
        Assert.Equal(before.Rows, source.Snapshot().Rows);
        Assert.Equal(before.Files, source.Snapshot().Files);
        using var saved = builder.Freeze(draft);
        var contracts = new NexusPipeline.Platform.Serialization.ContractReader();
        var blobs = new ImmutableBlobStore(source.Instance, contracts);
        var codec = new ExecutionPlanCodec(source.Instance, contracts, blobs);
        var stored = codec.Read(saved.Reference).ReadWire();
        Assert.Equal(preview.Steps.Select(step => step.StepId), stored.Steps.Select(step => step.StepId));
        Assert.All(stored.Steps, step => Assert.Equal(previewProviders[0].Manifest, step.Provider!.Manifest));
        Assert.Equal(before.Rows, source.Snapshot().Rows);

        var end = stored.Steps[^1];
        var changed = end with { ImageSha256 = new string('0', 64) };
        var bad = stored with { Steps = [stored.Steps[0], stored.Steps[1], changed] };
        var error = Assert.Throws<IOException>(() => codec.Freeze(bad).Dispose());
        Assert.Equal("execution_provider_worker_unbound", error.Message);
    }

    private static ExecutionPlanBuilder Builder(PlanBuilderFixture source, ExecutionProviderRegistry registry) =>
        new(source.Instance, source.Database, new(), source.Lease, new(5, 6, 7), null, registry);

    private static void MakeSpecialized(PlanBuilderFixture source)
    {
        var original = JudgementInterpreterLease.Inspect(Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!);
        foreach (var relative in original.RuntimeFiles.Select(file => file.RelativePath))
        {
            var destination = Path.Combine(Path.GetDirectoryName(source.OtherImage)!, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(Path.GetDirectoryName(original.Path)!, relative), destination, overwrite: true);
        }
        var catalog = source.Definitions.Read(); var script = catalog.Scripts.Single(script => script.Id == source.OtherScriptId);
        var changed = script with { Kind = AutomationKind.Specialized, ExtensionId = "provider-fixture",
            ConfigurationPaths = [], LogPaths = [], UpdateConfigurationAtFinish = true };
        var change = new AutomationChanges(catalog.Revision, [], [changed], [], [], [], [], []);
        source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "provider-input-fixture", change), change, PlanBuilderFixture.CreatedAt);
    }
}
