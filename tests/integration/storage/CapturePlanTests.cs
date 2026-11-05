using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class CapturePlanTests
{
    [Theory(DisplayName = "An Original Pc Plan Freezes And Holds Its Actual Capture Broker And Every Runtime Asset Before Admission")]
    [InlineData(0)]
    [InlineData(300)]
    public void HoldsCaptureRuntime(int runtimeFiles)
    {
        using var fixture = new CapturePlanFixture(runtimeFiles);
        using var draft = fixture.Prepare();
        var plan = Assert.IsType<FrozenExecutionPlan>(draft.ReadPlan()).ReadWire();
        var runtime = Assert.IsType<ExecutionPlanCaptureRuntime>(plan.CaptureRuntime);
        Assert.Equal(fixture.Broker, runtime.Broker.Path); Assert.NotEmpty(runtime.Broker.RuntimeFiles);
        if (runtimeFiles > 0) Assert.Equal(runtimeFiles, runtime.Broker.RuntimeFiles.Count);
        Assert.Equal(5000, runtime.TimeoutMilliseconds); Assert.Equal(8388608, runtime.MaximumPngBytes);
        Assert.Equal(1, runtime.MaximumFramesPerStep);
        var claims = ExecutionPlanResources.Claims(plan);
        foreach (var path in ExecutionCaptureInputs.Paths(plan))
            Assert.All(PhysicalResourceAdmission.Read(path, ResourceClaimMode.Reference), expected => Assert.Contains(expected, claims));
        var held = new List<IDisposable>();
        try
        {
            PlanInputLease.Acquire(plan, held);
            foreach (var path in ExecutionCaptureInputs.Paths(plan))
                Assert.ThrowsAny<IOException>(() => new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());
        }
        finally { for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose(); }
    }

    [Fact(DisplayName = "A Capture Broker Runtime Beyond The Bounded Manifest Capacity Is Rejected Before Admission")]
    public void RejectsOversizedRuntime()
    {
        using var fixture = new CapturePlanFixture(513);
        var error = Assert.Throws<IOException>(() => fixture.Prepare());
        Assert.Equal("judgement_runtime_asset_limit", error.Message);
    }

    [Fact(DisplayName = "A Changed Capture Broker Cannot Acquire The Original Plan Input Lease And Historical Metadata Does Not Reopen It")]
    public void RejectsChangedBroker()
    {
        using var fixture = new CapturePlanFixture();
        using var draft = fixture.Prepare();
        var plan = Assert.IsType<FrozenExecutionPlan>(draft.ReadPlan()).ReadWire();
        File.WriteAllText(fixture.Broker, "preserve-changed-capture-image");
        ExecutionCaptureInputs.Require(plan);
        var held = new List<IDisposable>();
        try { Assert.ThrowsAny<IOException>(() => PlanInputLease.Acquire(plan, held)); }
        finally { for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose(); }
        Assert.Equal("preserve-changed-capture-image", File.ReadAllText(fixture.Broker));
    }

    [Theory(DisplayName = "A Stored Capture Runtime Cannot Change Its Original Bounds Assets Or Target Kind")]
    [InlineData("timeout")]
    [InlineData("bytes")]
    [InlineData("frames")]
    [InlineData("assets")]
    [InlineData("target")]
    public void FreezesRuntimeBounds(string damage)
    {
        using var fixture = new CapturePlanFixture();
        using var draft = fixture.Prepare();
        var plan = Assert.IsType<FrozenExecutionPlan>(draft.ReadPlan()).ReadWire();
        var runtime = plan.CaptureRuntime!;
        plan = damage switch
        {
            "timeout" => plan with { CaptureRuntime = runtime with { TimeoutMilliseconds = 5001 } },
            "bytes" => plan with { CaptureRuntime = runtime with { MaximumPngBytes = 8388609 } },
            "frames" => plan with { CaptureRuntime = runtime with { MaximumFramesPerStep = 2 } },
            "assets" => plan with { CaptureRuntime = runtime with { Broker = runtime.Broker with { RuntimeFiles = [] } } },
            _ => plan with { Steps = plan.Steps.Select(step => step with { Target = null }).ToArray() }
        };
        Assert.ThrowsAny<IOException>(() => ExecutionCaptureInputs.Require(plan));
    }

    private sealed class CapturePlanFixture : IDisposable
    {
        internal PlanBuilderFixture Source { get; } = new();
        internal string Broker { get; }
        private readonly ExecutionPlanBuilder _builder;

        internal CapturePlanFixture(int runtimeFiles = 0)
        {
            var image = ExecutionHookImages.Capture(Environment.GetEnvironmentVariable("NEXUS_TEST_HOST_EXE")!, true);
            var directory = Path.Combine(Source.Root, "capture-host"); Directory.CreateDirectory(directory);
            Broker = Path.Combine(directory, Path.GetFileName(image.Path)); File.Copy(image.Path, Broker);
            foreach (var file in image.RuntimeFiles)
            {
                var target = Path.Combine(directory, file.RelativePath); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(Path.GetDirectoryName(image.Path)!, file.RelativePath), target);
            }
            if (runtimeFiles > 0)
            {
                var manifestPath = Path.ChangeExtension(Broker, ".deps.json");
                var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
                var target = manifest["runtimeTarget"]!["name"]!.GetValue<string>();
                var assets = new JsonObject();
                for (var index = image.RuntimeFiles.Count; index < runtimeFiles; index++)
                {
                    var relative = $"capture-fixture-asset-{index:D4}.bin";
                    File.WriteAllText(Path.Combine(directory, relative), "captured runtime asset");
                    assets[relative] = new JsonObject();
                }
                manifest["targets"]![target]!["CaptureFixture.Assets/1.0.0"] = new JsonObject { ["runtime"] = assets };
                File.WriteAllText(manifestPath, manifest.ToJsonString());
            }
            var catalog = Source.Definitions.Read(); var script = catalog.Scripts.Single(item => item.Id == Source.ScriptId);
            script = script with { Target = new(AutomationTargetKind.Pc, false, 5, false,
                new(script.Executable, [], script.Executable, AutomationPcReadiness.Process), null) };
            var changes = new AutomationChanges(catalog.Revision, [], [script], [], [], [], [], []);
            Source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "capture-plan-fixture", changes), changes, DateTimeOffset.UtcNow);
            _builder = new(Source.Instance, Source.Database, new(), Source.Lease, new(5, 6, 7), new(Broker, null));
        }

        internal ExecutionPlanDraft Prepare() => _builder.Prepare(Source.Request(new BindingTarget(Source.BindingA)), PlanBuilderFixture.CreatedAt);
        public void Dispose() => Source.Dispose();
    }
}
