using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class AttemptPlanTests
{
    [Fact(DisplayName = "Attempt Intent Binds A Real Total Plan And Keeps Its Definition And Every Plan Leased")]
    public void LeasesBoundPlans()
    {
        var fixture = ExecutionPlanFixture.Create();
        using var total = fixture.Codec.Freeze(fixture.Plan);
        var plan = AttemptPlanSeed.Create(fixture, total);
        var codec = new AttemptPlanCodec(fixture.Instance, new(), fixture.Blobs);
        using var prepared = codec.Freeze(plan);
        Assert.Equal(prepared.Plan.Export(), codec.Read(prepared.Reference).Export());
        var exported = prepared.Plan.Export();
        exported[0] = 0;
        Assert.Equal(plan.RunId, prepared.Plan.ReadWire().RunId);
        total.Dispose();
        foreach (var path in new[] { plan.ExecutionPlan.RelativePath, fixture.Plan.DefinitionBundle.RelativePath, prepared.Reference.RelativePath })
            Assert.Throws<IOException>(() => File.Delete(Path.Combine(fixture.Instance.RuntimeRoot, path)));
        Assert.False(Directory.Exists(Path.Combine(fixture.Instance.RuntimeRoot, plan.OutputDirectory)));
        Assert.False(Directory.Exists(Path.Combine(fixture.Instance.RuntimeRoot, plan.LogDirectory)));
        prepared.Dispose();
        prepared.Dispose();
        Assert.Throws<ObjectDisposedException>(() => prepared.Plan);
        Assert.Throws<ObjectDisposedException>(() => prepared.Reference);
    }

    [Theory(DisplayName = "Attempt Intent Cannot Borrow Another Run Scope Configuration Or Output Directory")]
    [InlineData("instance")]
    [InlineData("run")]
    [InlineData("ordinal")]
    [InlineData("scope")]
    [InlineData("output")]
    [InlineData("log")]
    [InlineData("configuration")]
    [InlineData("empty-id")]
    public void RejectsBorrowedScopes(string fault)
    {
        var fixture = ExecutionPlanFixture.Create();
        using var total = fixture.Codec.Freeze(fixture.Plan);
        var plan = AttemptPlanSeed.Create(fixture, total);
        plan = fault switch
        {
            "instance" => plan with { InstanceId = Guid.NewGuid().ToString("D") },
            "run" => plan with { RunId = Guid.NewGuid().ToString("D") },
            "ordinal" => plan with { AttemptOrdinal = 2 },
            "scope" => plan with { ResourceKeys = ["unrelated"] },
            "output" => plan with { OutputDirectory = "runs/unrelated" },
            "log" => plan with { LogDirectory = "logs/unrelated" },
            "configuration" => plan with { ConfigurationPlans = [new() { StepId = "first", Plan = plan.ExecutionPlan }] },
            _ => plan with { AttemptId = Guid.Empty.ToString("D") }
        };
        var directory = Path.Combine(fixture.Instance.RuntimeRoot, "blobs", "plans");
        var before = Directory.GetFiles(directory).Order().ToArray();
        Assert.ThrowsAny<Exception>(() => new AttemptPlanCodec(fixture.Instance, new ContractReader(), fixture.Blobs).Freeze(plan));
        Assert.Equal(before, Directory.GetFiles(directory).Order().ToArray());
        Assert.Equal(new byte[] { 0, 1, 255 }, File.ReadAllBytes(fixture.Image));
    }

}
