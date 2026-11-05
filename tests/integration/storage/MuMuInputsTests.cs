using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;
using Target = NexusPipeline.Core.Automation.AutomationTarget;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class MuMuInputsTests
{
    [Theory(DisplayName = "The Saved Mu Mu Definition And Public Bundle Round Trip The Explicit Pairing Without Starting Either Tool")]
    [InlineData(false)]
    [InlineData(true)]
    public void RoundTripsPairing(bool configured)
    {
        using var fixture = new MuMuDiscoveryFixture();
        var (root, target) = Definition(fixture, configured);
        var source = fixture.Base.Source; var catalog = source.Definitions.Read();
        var changes = new AutomationChanges(catalog.Revision, [root],
            [catalog.Scripts.Single(script => script.Id == source.ScriptId) with { Target = target }], [], [], [], [], []);
        source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "mumu-paired-input", changes),
            changes, PlanBuilderFixture.CreatedAt);
        var saved = new AutomationDefinitionStore(source.Database).Read().Scripts.Single(script => script.Id == source.ScriptId).Target!;
        Assert.Equal(target.Android!.ScreenshotTool, saved.Android!.ScreenshotTool);
        var wire = AutomationTargetCodec.ToWire(saved)!;
        using var valid = new ContractReader().Read(JsonSerializer.SerializeToUtf8Bytes(wire), "automation-target.schema.json");
        Assert.Equal(saved.Android.ScreenshotTool, AutomationTargetCodec.FromWire(wire)!.Android!.ScreenshotTool);
        var persisted = AutomationTargetCodec.Encode(saved)!;
        Assert.Equal(persisted, AutomationTargetCodec.Encode(AutomationTargetCodec.Decode(persisted)));
        var expected = JsonSerializer.SerializeToNode(saved)!;
        expected["Android"]!.AsObject().Remove("Emulator");
        if (!configured) expected["Android"]!.AsObject().Remove("ScreenshotTool");
        Assert.Equal(expected.ToJsonString(), persisted);
        Assert.False(File.Exists(fixture.Marker + ".started"));
        var before = fixture.Base.Counts();
        Assert.Throws<IOException>(() => ExecutionTargetInputs.Capture(target,
            new Dictionary<Guid, AutomationRoot> { [root.Id] = root }, new(source.Instance, new()), source.Instance.InstanceId));
        Assert.Equal(before, fixture.Base.Counts());
        Assert.False(File.Exists(fixture.Marker + ".started"));
    }

    [Fact(DisplayName = "An Explicit Stopped Discovery Freezes And Holds Every Paired Image Asset And Rejects Source Rebinding")]
    public async Task FreezesDiscoveredAssets()
    {
        using var fixture = new MuMuDiscoveryFixture();
        using var discovery = fixture.Prepare();
        await discovery.DiscoverAsync(TimeSpan.FromSeconds(15));
        var (root, definition) = Definition(fixture, configured: true);
        var source = fixture.Base.Source;
        var frozen = ExecutionTargetInputs.Capture(definition, new Dictionary<Guid, AutomationRoot> { [root.Id] = root },
            new(source.Instance, new()), source.Instance.InstanceId, discovery)!;
        var paired = Assert.IsType<ExecutionPlanInterpreterImage>(frozen.Android!.ScreenshotTool);
        Assert.NotEqual(fixture.Manager, paired.Path); Assert.NotEmpty(paired.RuntimeFiles);
        Assert.NotEqual(PhysicalObjectIdentity.Read(fixture.Manager), PhysicalObjectIdentity.Read(paired.Path));
        Assert.Equal(frozen.Android.Tool.Sha256, paired.Sha256);
        var physical = PhysicalObjectIdentity.Read(root.Path);
        var rootBinding = new ExecutionPlanRootBindingsItem { RootId = root.Id.ToString("D"), Path = root.Path,
            Identity = new() { VolumeSerial = physical.VolumeSerial, FileId = physical.FileId } };
        var roots = new Dictionary<string, ExecutionPlanRootBindingsItem> { [rootBinding.RootId] = rootBinding };
        ExecutionTargetInputs.Require(frozen, AutomationTargetCodec.ToWire(definition), roots);
        Assert.Throws<IOException>(() => ExecutionTargetInputs.Require(
            frozen with { Android = frozen.Android with { ScreenshotTool = null } }, AutomationTargetCodec.ToWire(definition), roots));
        using var draft = source.Prepare(new BindingTarget(source.OtherBinding));
        var original = Assert.IsType<FrozenExecutionPlan>(draft.ReadPlan()).ReadWire();
        var plan = original with { Steps = [original.Steps.Single() with { Target = frozen }],
            RootBindings = original.RootBindings.Append(rootBinding).ToArray() };
        plan = plan with { PhysicalClaims = ExecutionPlanResources.Capture(plan) };
        var claims = ExecutionPlanResources.Claims(plan);
        foreach (var file in ExecutionTargetInputs.Files(plan.Steps.Single()))
            Assert.All(PhysicalResourceAdmission.Read(file.Path, ResourceClaimMode.Reference), claim => Assert.Contains(claim, claims));
        discovery.Dispose();
        var held = new List<IDisposable>();
        try
        {
            PlanInputLease.Acquire(plan, held);
            foreach (var file in ExecutionTargetInputs.Files(plan.Steps.Single()))
                Assert.ThrowsAny<IOException>(() => new FileStream(file.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());
        }
        finally { for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose(); }
        var runtime = paired.RuntimeFiles.First(file => file.RelativePath.EndsWith(".runtimeconfig.json", StringComparison.Ordinal));
        var path = Path.Combine(Path.GetDirectoryName(paired.Path)!, runtime.RelativePath);
        var originalManagerRuntime = Path.Combine(Path.GetDirectoryName(fixture.Manager)!, runtime.RelativePath);
        Assert.NotEqual(originalManagerRuntime, path);
        var managerBytes = File.ReadAllBytes(originalManagerRuntime);
        File.WriteAllText(path, "changed-paired-runtime");
        held.Clear();
        try { Assert.ThrowsAny<IOException>(() => PlanInputLease.Acquire(plan, held)); }
        finally { for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose(); }
        Assert.Equal("changed-paired-runtime", File.ReadAllText(path));
        Assert.Equal(managerBytes, File.ReadAllBytes(originalManagerRuntime));
        Assert.NotEmpty(ExecutionTargetInputs.MappingReferences(plan, new(source.Instance, new()), verifyContent: true));
    }

    [Fact(DisplayName = "Both Public Schemas Reject A Paired Tool On Generic Adb Without Changing The Original Definitions")]
    public async Task RejectsGenericPairing()
    {
        using var fixture = new MuMuDiscoveryFixture();
        using var discovery = fixture.Prepare();
        await discovery.DiscoverAsync(TimeSpan.FromSeconds(15));
        var (root, definition) = Definition(fixture, configured: true);
        var wire = AutomationTargetCodec.ToWire(definition)!;
        wire = wire with { Android = wire.Android! with { Driver = "genericAdb" } };
        var reader = new ContractReader();
        Assert.Throws<JsonException>(() => reader.Read(JsonSerializer.SerializeToUtf8Bytes(wire), "automation-target.schema.json"));
        var source = fixture.Base.Source;
        var frozen = ExecutionTargetInputs.Capture(definition, new Dictionary<Guid, AutomationRoot> { [root.Id] = root },
            new(source.Instance, new()), source.Instance.InstanceId, discovery)!;
        frozen = frozen with { Android = frozen.Android! with { Driver = "genericAdb", Mapping = null } };
        Assert.Throws<JsonException>(() => reader.Read(JsonSerializer.SerializeToUtf8Bytes(frozen), "execution-target.schema.json"));
        Assert.NotNull(definition.Android!.ScreenshotTool);
    }

    private static (AutomationRoot Root, Target Target) Definition(MuMuDiscoveryFixture fixture, bool configured)
    {
        var root = new AutomationRoot(Guid.NewGuid(), 0, "Explicit paired tool", AutomationRootKind.External, Path.GetDirectoryName(fixture.Manager)!);
        string? screenshot = null;
        if (configured)
        {
            var image = ExecutionHookImages.Capture(fixture.Manager, runtime: true);
            var directory = Path.Combine(root.Path, "paired screenshot 独立");
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, Path.GetFileName(fixture.Manager));
            File.Copy(fixture.Manager, target);
            foreach (var runtime in image.RuntimeFiles)
            {
                var destination = Path.Combine(directory, runtime.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(Path.Combine(root.Path, runtime.RelativePath), destination);
            }
            screenshot = "paired screenshot 独立/" + Path.GetFileName(fixture.Manager);
        }
        var android = new AutomationAndroidTarget(AutomationAndroidDriver.Mumu, new(root.Id, Path.GetFileName(fixture.Manager)),
            "127.0.0.1:16384", "com.nexus.fixture", ["-n", "com.nexus.fixture/.Main"])
        { ScreenshotTool = screenshot is null ? null : new(root.Id, screenshot) };
        return (root, new(AutomationTargetKind.Android, true, 15, false, null, android));
    }
}
