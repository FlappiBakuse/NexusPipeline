using System.Text.Json;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class ExecutionPlanTests
{
    [Fact(DisplayName = "Repeated Frozen Executable And Runtime Inputs Share Protection Until The Plan Lease Is Released")]
    public void SharesRepeatedInputs()
    {
        var fixture = ExecutionPlanFixture.Create();
        var step = fixture.Plan.Steps[0];
        var runtimePath = Path.Combine(step.WorkingDirectory, "shared.runtime");
        File.WriteAllBytes(runtimePath, [3, 2, 1]);
        var runtime = new ExecutionPlanRuntimeFile
        {
            RelativePath = Path.GetFileName(runtimePath), SizeBytes = 3,
            Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(runtimePath))),
            Identity = FileTreeProtocol.ToWire(PhysicalObjectIdentity.Read(runtimePath)), ParentIdentity = step.ParentIdentity
        };
        var image = new ExecutionPlanInterpreterImage
        {
            Path = step.ExecutablePath, SizeBytes = step.ImageSizeBytes, Sha256 = step.ImageSha256,
            Identity = step.ImageIdentity, ParentIdentity = step.ParentIdentity, RuntimeFiles = [runtime]
        };
        var plan = fixture.Plan with
        {
            CaptureRuntime = new() { Broker = image, TimeoutMilliseconds = 5000, MaximumPngBytes = 8388608, MaximumFramesPerStep = 1 },
            Steps = [step with { JudgementWorker = new() { Broker = image, ScriptSha256 = step.ImageSha256,
                MaximumInvocations = 1, MaximumEvidenceBytes = 1024 } }, step with { StepId = "second" }]
        };
        var held = new List<IDisposable>();
        try
        {
            PlanInputLease.Acquire(plan, held);
            foreach (var path in new[] { fixture.Image, runtimePath })
            {
                Assert.Throws<IOException>(() => File.WriteAllBytes(path, [9]));
                Assert.Throws<IOException>(() => File.Delete(path));
            }
            Assert.Throws<IOException>(() => Directory.Move(step.WorkingDirectory, step.WorkingDirectory + "-moved"));
            Assert.Equal(new byte[] { 0, 1, 255 }, File.ReadAllBytes(fixture.Image));
            Assert.Equal(new byte[] { 3, 2, 1 }, File.ReadAllBytes(runtimePath));
        }
        finally { for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose(); }
        File.WriteAllBytes(fixture.Image, [9]); File.WriteAllBytes(runtimePath, [8]);
        var moved = step.WorkingDirectory + "-moved";
        Directory.Move(step.WorkingDirectory, moved);
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(Path.Combine(moved, Path.GetFileName(fixture.Image))));
        Assert.Equal(new byte[] { 8 }, File.ReadAllBytes(Path.Combine(moved, Path.GetFileName(runtimePath))));
    }

    [Theory(DisplayName = "Shared Plan Inputs Reject Conflicting Size Digest File Or Parent Identity And Release All Protection")]
    [InlineData("size", false)]
    [InlineData("digest", false)]
    [InlineData("file", false)]
    [InlineData("parent", false)]
    [InlineData("size", true)]
    [InlineData("digest", true)]
    [InlineData("file", true)]
    [InlineData("parent", true)]
    public void RejectsConflictingSharedInputs(string fault, bool captureFirst)
    {
        var fixture = ExecutionPlanFixture.Create();
        var step = fixture.Plan.Steps[0];
        var conflict = fault switch
        {
            "size" => step with { ImageSizeBytes = step.ImageSizeBytes + 1 },
            "digest" => step with { ImageSha256 = new('0', 64) },
            "file" => step with { ImageIdentity = step.ParentIdentity },
            _ => step with { ParentIdentity = step.ImageIdentity }
        };
        var plan = fixture.Plan with { Steps = captureFirst ? [conflict] : [step, conflict with { StepId = "second" }] };
        if (captureFirst) plan = plan with
        {
            CaptureRuntime = new()
            {
                Broker = new() { Path = step.ExecutablePath, SizeBytes = step.ImageSizeBytes, Sha256 = step.ImageSha256,
                    Identity = step.ImageIdentity, ParentIdentity = step.ParentIdentity, RuntimeFiles = [] },
                TimeoutMilliseconds = 5000, MaximumPngBytes = 8388608, MaximumFramesPerStep = 1
            }
        };
        var held = new List<IDisposable>();
        try { Assert.Throws<IOException>(() => PlanInputLease.Acquire(plan, held)); }
        finally { for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose(); }
        Assert.Equal(new byte[] { 0, 1, 255 }, File.ReadAllBytes(fixture.Image));
        File.WriteAllBytes(fixture.Image, [9]);
        Directory.Move(step.WorkingDirectory, step.WorkingDirectory + "-moved");
    }

    [Fact(DisplayName = "Frozen Execution Plan Keeps Independent Bytes And Leases Its Real Images And Definition")]
    public void OwnsFrozenImages()
    {
        var fixture = ExecutionPlanFixture.Create();
        var mutableSteps = fixture.Plan.Steps.ToArray();
        using var prepared = fixture.Codec.Freeze(fixture.Plan with { Steps = mutableSteps });
        var bytes = prepared.Plan.Export();
        var wire = prepared.Plan.ReadWire();
        mutableSteps[0] = fixture.Plan.Steps[0] with { Arguments = ["changed"] };
        ((IList<string>)wire.Steps[0].Arguments)[0] = "consumer changed";
        bytes[0] = 0;
        Assert.Equal("--fixture", prepared.Plan.ReadWire().Steps[0].Arguments[0]);
        Assert.Equal(prepared.Plan.Export(), fixture.Codec.Read(prepared.Reference).Export());
        foreach (var path in new[] { fixture.Image, Path.Combine(fixture.Instance.RuntimeRoot, prepared.Reference.RelativePath),
            Path.Combine(fixture.Instance.RuntimeRoot, fixture.Plan.DefinitionBundle.RelativePath) })
        {
            Assert.Throws<IOException>(() => File.Delete(path));
            Assert.Throws<IOException>(() => { using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite); });
        }
        prepared.Dispose();
        prepared.Dispose();
        Assert.Throws<ObjectDisposedException>(() => prepared.Plan);
        Assert.Throws<ObjectDisposedException>(() => prepared.Reference);
    }

    [Theory(DisplayName = "Unbound Execution Plans Cannot Publish Plan Bytes Or Change Any Original File")]
    [InlineData("step-cycle")]
    [InlineData("task-cycle")]
    [InlineData("missing-task")]
    [InlineData("disabled-task")]
    [InlineData("unsafe-retry")]
    [InlineData("unapproved-retry")]
    [InlineData("deadline")]
    [InlineData("scope")]
    [InlineData("argument")]
    [InlineData("revision")]
    [InlineData("binding")]
    [InlineData("completion")]
    [InlineData("image-size")]
    [InlineData("image-identity")]
    [InlineData("root-identity")]
    public void RejectsUnboundPlans(string fault)
    {
        var fixture = ExecutionPlanFixture.Create();
        _ = fixture.Codec.Inspect(fixture.Plan, JsonSerializer.SerializeToUtf8Bytes(fixture.Bundle));
        var plan = fixture.Plan;
        var step = plan.Steps[0];
        var task = step.Tasks[0];
        plan = fault switch
        {
            "step-cycle" => plan with { Steps = [step with { DependsOn = [step.StepId] }] },
            "task-cycle" => plan with { Steps = [step with { Tasks = [task with { Dependencies = [task.TaskId] }] }] },
            "missing-task" => plan with { Steps = [step with { SelectedTaskIds = ["missing"] }] },
            "disabled-task" => plan with { Steps = [step with { Tasks = [task with { IsEnabled = false }] }] },
            "unsafe-retry" => plan with { RetryPolicy = plan.RetryPolicy with { Safety = "safe" } },
            "unapproved-retry" => plan with { RetryPolicy = plan.RetryPolicy with { MaximumAttempts = 2, HostApproved = false } },
            "deadline" => plan with { RetryPolicy = plan.RetryPolicy with { DeadlineUtc = "2026-10-19T00:03:00Z" } },
            "scope" => plan with { ResourceKeys = ["unrelated"] },
            "argument" => plan with { Steps = [step with { Arguments = ["unrelated"] }] },
            "revision" => plan with { SourceRevisions = [] },
            "binding" => plan with { Steps = [step with { BindingId = Guid.NewGuid().ToString("D") }] },
            "completion" => plan with { CompletionAction = "shutdown" },
            "image-size" => plan with { Steps = [step with { ImageSizeBytes = 4 }] },
            "image-identity" => plan with { Steps = [step with { ImageIdentity = step.ParentIdentity }] },
            _ => plan with { RootBindings = [plan.RootBindings[0] with { Identity = step.ImageIdentity }] }
        };
        var before = File.ReadAllBytes(fixture.Image);
        Assert.ThrowsAny<Exception>(() => fixture.Codec.Freeze(plan));
        Assert.Equal(before, File.ReadAllBytes(fixture.Image));
        Assert.False(Directory.Exists(Path.Combine(fixture.Instance.RuntimeRoot, "blobs", "plans")));
    }

    [Theory(DisplayName = "Actual Definition Relations Are Validated Instead Of Trusting Copied Launch Fields")]
    [InlineData("disabled-binding")]
    [InlineData("duplicate-script")]
    public void ValidatesDefinitionRelations(string fault)
    {
        var fixture = ExecutionPlanFixture.Create();
        _ = fixture.Codec.Inspect(fixture.Plan, JsonSerializer.SerializeToUtf8Bytes(fixture.Bundle));
        var bundle = fixture.Bundle.DeepClone().AsObject();
        if (fault == "disabled-binding") bundle["bindings"]![0]!["enabled"] = false;
        else bundle["scripts"]!.AsArray().Add(bundle["scripts"]![0]!.DeepClone());
        Assert.Throws<IOException>(() => fixture.Codec.Freeze(fixture.WithBundle(bundle)));
        Assert.False(Directory.Exists(Path.Combine(fixture.Instance.RuntimeRoot, "blobs", "plans")));
    }

    [Theory(DisplayName = "Historical Plans Remain Readable After Source Junctions While New Plans And Attempts Reject Them")]
    [InlineData(false)]
    [InlineData(true)]
    public void PreservesHistoricalLinks(bool broken)
    {
        var fixture = ExecutionPlanFixture.Create();
        var prepared = fixture.Codec.Freeze(fixture.Plan);
        var reference = prepared.Reference;
        var bytes = prepared.Plan.Export();
        var attempt = AttemptPlanSeed.Create(fixture, prepared);
        prepared.Dispose();
        var directory = Path.GetDirectoryName(fixture.Image)!;
        var preserved = Path.Combine(fixture.Root, "preserved-script");
        Directory.Move(directory, preserved);
        ControlledJunction.Create(directory, broken ? Path.Combine(fixture.Root, "absent-script") : preserved);
        try
        {
            Assert.Equal(bytes, fixture.Codec.Read(reference).Export());
            Assert.Throws<IOException>(() => fixture.Codec.Inspect(fixture.Plan, JsonSerializer.SerializeToUtf8Bytes(fixture.Bundle)));
            Assert.Throws<IOException>(() => fixture.Codec.Freeze(fixture.Plan));
            var codec = new NexusPipeline.Platform.Storage.Execution.AttemptPlanCodec(fixture.Instance, new(), fixture.Blobs);
            Assert.Throws<IOException>(() => codec.Freeze(attempt));
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(fixture.Instance.RuntimeRoot, reference.RelativePath)));
        }
        finally
        {
            Directory.Delete(directory);
            Directory.Move(preserved, directory);
        }
        using var restored = fixture.Codec.Freeze(fixture.Plan);
        Assert.Equal(bytes, restored.Plan.Export());
    }

    [Fact(DisplayName = "Reading Historical Intent Never Executes It And Refreezing Detects An Equal Byte Image Replacement")]
    public void PreservesHistoricalIntent()
    {
        var fixture = ExecutionPlanFixture.Create();
        var prepared = fixture.Codec.Freeze(fixture.Plan);
        var reference = prepared.Reference;
        var before = prepared.Plan.Export();
        prepared.Dispose();
        File.Move(fixture.Image, Path.Combine(fixture.Root, "original.bin"));
        File.WriteAllBytes(fixture.Image, [0, 1, 255]);
        Assert.Equal(before, fixture.Codec.Read(reference).Export());
        Assert.Throws<IOException>(() => fixture.Codec.Freeze(fixture.Plan));
        var invalid = fixture.Blobs.PersistPlan(JsonSerializer.SerializeToUtf8Bytes(fixture.Plan with { Version = 9 }));
        var invalidBytes = File.ReadAllBytes(Path.Combine(fixture.Instance.RuntimeRoot, invalid.RelativePath));
        Assert.Throws<JsonException>(() => fixture.Codec.Read(invalid));
        Assert.Equal(invalidBytes, File.ReadAllBytes(Path.Combine(fixture.Instance.RuntimeRoot, invalid.RelativePath)));
    }
}
