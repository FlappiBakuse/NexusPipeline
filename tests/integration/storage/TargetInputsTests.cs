using System.Collections.Immutable;
using System.Runtime.Versioning;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;
using WireTarget = NexusPipeline.Platform.Serialization.Protocol.ExecutionTarget;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class TargetInputsTests
{
    [Fact(DisplayName = "Dry Run With A Mu Mu Declaration Never Starts Info And Requires Explicit Discovery")]
    public void RequiresExplicitDiscovery()
    {
        using var fixture = new MuMuDiscoveryFixture();
        var (root, target) = Definition(fixture);
        var source = fixture.Base.Source;
        var catalog = source.Definitions.Read();
        var changes = new AutomationChanges(catalog.Revision, [root],
            [catalog.Scripts.Single(script => script.Id == source.ScriptId) with { Target = target }], [], [], [], [], []);
        source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "target-input-fixture", changes),
            changes, PlanBuilderFixture.CreatedAt);
        var before = fixture.Base.Counts();
        using var draft = source.Prepare(new BindingTarget(source.BindingA));
        Assert.Null(draft.ReadPlan());
        Assert.Contains(draft.Explanation.Items, item => item.ReasonCode == "execution_mumu_discovery_required");
        Assert.False(File.Exists(fixture.Marker + ".started"));
        Assert.Equal(before, fixture.Base.Counts());
        var blobs = new ImmutableBlobStore(source.Instance, new());
        Assert.Throws<IOException>(() => ExecutionTargetInputs.Capture(target, new Dictionary<Guid, AutomationRoot> { [root.Id] = root },
            blobs, source.Instance.InstanceId));
        Assert.False(File.Exists(fixture.Marker + ".started"));
        Assert.Equal(before, fixture.Base.Counts());
    }

    [Fact(DisplayName = "The Frozen Mapping Uses The Explicit Stopped Discovery And Keeps Its Immutable Reference Closure")]
    public async Task FreezesStoppedMapping()
    {
        using var fixture = new MuMuDiscoveryFixture();
        using var discovery = fixture.Prepare();
        await discovery.DiscoverAsync(TimeSpan.FromSeconds(15));
        var before = fixture.Base.Counts();
        var (root, definition) = Definition(fixture);
        var blobs = new ImmutableBlobStore(fixture.Base.Source.Instance, new());
        var target = Assert.IsType<WireTarget>(ExecutionTargetInputs.Capture(definition,
            new Dictionary<Guid, AutomationRoot> { [root.Id] = root }, blobs, fixture.Base.Source.Instance.InstanceId, discovery));
        Assert.Equal("mumu", target.Android!.Driver);
        Assert.Equal(3, target.Android.Mapping!.Index);
        Assert.Equal(16384, target.Android.Mapping.EndpointPort);
        using var draft = fixture.Base.Source.Prepare(new BindingTarget(fixture.Base.Source.OtherBinding));
        var original = Assert.IsType<FrozenExecutionPlan>(draft.ReadPlan()).ReadWire();
        var plan = original with { Steps = [original.Steps.Single() with { Target = target }] };
        var references = ExecutionTargetInputs.MappingReferences(plan, blobs, verifyContent: true);
        Assert.True(references.Count >= 3);
        Assert.True(references.SequenceEqual(ExecutionTargetInputs.MappingReferences(plan, blobs, verifyContent: false)));
        Assert.All(references, reference => blobs.Verify(reference));
        var mappingReference = target.Android.Mapping.InfoEvidence;
        var saved = MuMuDiscoveryEvidence.Read(blobs, new(mappingReference.RelativePath, mappingReference.SizeBytes, mappingReference.Sha256));
        var stdout = saved.Parts.First(part => part.Source == NexusPipeline.Platform.Processes.ProcessOutputSource.StandardOutput).Content;
        discovery.Dispose();
        using (var exclusive = new FileStream(Path.Combine(fixture.Base.Source.Instance.RuntimeRoot, stdout.RelativePath),
            FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(references, ExecutionTargetInputs.MappingReferences(plan, blobs, verifyContent: false));
            Assert.Throws<IOException>(() => ExecutionTargetInputs.MappingReferences(plan, blobs, verifyContent: true));
        }
        var physical = ExecutionPlanResources.Capture(plan);
        var claims = ExecutionPlanResources.Claims(plan with { PhysicalClaims = physical });
        Assert.Contains(claims, claim => claim.ResourceKey == TargetResourceKeys.AndroidControl &&
            claim.Mode == NexusPipeline.Core.Maintenance.ResourceClaimMode.Mutation);
        Assert.Contains(claims, claim => claim.ResourceKey == "target:android:endpoint:127.0.0.1:16384" &&
            claim.Mode == NexusPipeline.Core.Maintenance.ResourceClaimMode.Mutation);
        Assert.Equal(before, fixture.Base.Counts());
    }

    [Theory(DisplayName = "A Stored Mapping Cannot Be Rebound To Another Index Endpoint Manager Or Instance")]
    [InlineData("index")]
    [InlineData("endpoint")]
    [InlineData("manager")]
    [InlineData("instance")]
    public async Task RejectsReboundMappings(string damage)
    {
        using var fixture = new MuMuDiscoveryFixture();
        using var discovery = fixture.Prepare();
        await discovery.DiscoverAsync(TimeSpan.FromSeconds(15));
        var (root, definition) = Definition(fixture);
        var blobs = new ImmutableBlobStore(fixture.Base.Source.Instance, new());
        var target = ExecutionTargetInputs.Capture(definition, new Dictionary<Guid, AutomationRoot> { [root.Id] = root },
            blobs, fixture.Base.Source.Instance.InstanceId, discovery)!;
        using var draft = fixture.Base.Source.Prepare(new BindingTarget(fixture.Base.Source.OtherBinding));
        var original = Assert.IsType<FrozenExecutionPlan>(draft.ReadPlan()).ReadWire();
        var android = target.Android!;
        target = damage switch
        {
            "index" => target with { Android = android with { Mapping = android.Mapping! with { Index = 4 } } },
            "endpoint" => target with { Android = android with { Endpoint = "127.0.0.1:16385" } },
            "manager" => target with { Android = android with { Tool = android.Tool with { Sha256 = new string('a', 64) } } },
            _ => target
        };
        var plan = original with { Steps = [original.Steps.Single() with { Target = target }] };
        if (damage == "instance") plan = plan with { InstanceId = Guid.NewGuid().ToString("D") };
        var before = fixture.Base.Counts();
        var artifact = target.Android!.Mapping!.InfoEvidence;
        var bytes = File.ReadAllBytes(Path.Combine(fixture.Base.Source.Instance.RuntimeRoot, artifact.RelativePath));
        Assert.Throws<IOException>(() => ExecutionTargetInputs.MappingReferences(plan, blobs, verifyContent: true));
        Assert.Throws<IOException>(() => ExecutionTargetInputs.MappingReferences(plan, blobs, verifyContent: false));
        Assert.Equal(before, fixture.Base.Counts());
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(fixture.Base.Source.Instance.RuntimeRoot, artifact.RelativePath)));
    }

    private static (AutomationRoot Root, NexusPipeline.Core.Automation.AutomationTarget Target) Definition(MuMuDiscoveryFixture fixture)
    {
        var root = new AutomationRoot(Guid.NewGuid(), 0, "Explicit manager", AutomationRootKind.External, Path.GetDirectoryName(fixture.Manager)!);
        return (root, new(AutomationTargetKind.Android, true, 15, false, null,
            new(AutomationAndroidDriver.Mumu, new(root.Id, Path.GetFileName(fixture.Manager)), "127.0.0.1:16384",
                "com.nexus.fixture", ImmutableArray.Create("-n", "com.nexus.fixture/.Main"))));
    }
}
