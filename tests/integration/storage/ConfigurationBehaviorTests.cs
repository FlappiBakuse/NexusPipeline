using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage.Configuration;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ConfigurationBehaviorTests
{
    [Theory(DisplayName = "The First Ordinary Start Archives Valid Existing Configuration And Preserves Rejected Input Bytes")]
    [InlineData(true)]
    [InlineData(false)]
    public void EstablishesInitialSnapshot(bool valid)
    {
        using var fixture = new ExecutionAdmissionFixture(saved: false);
        var source = fixture.Source;
        if (!valid) File.WriteAllText(source.Configuration, "{invalid");
        var before = File.ReadAllBytes(source.Configuration);
        var operation = Guid.NewGuid();
        var request = source.Request(new BindingTarget(source.BindingA));
        if (valid)
        {
            fixture.Service.Start(operation, "initial-configuration", request, DateTimeOffset.UtcNow);
            using var accepted = fixture.Take(operation);
            var slot = Assert.Single(accepted.Prepared.Total.Plan.ReadWire().ConfigurationSlots);
            var saved = new ConfigurationRevisionStore(source.Instance, source.Database, new()).Read(new(source.BindingA, slot.ResourceId));
            Assert.NotNull(saved); Assert.Equal(1, saved.Revision);
            Assert.Equal(before.Length, Assert.Single(saved.Tree.Entries).SizeBytes);
        }
        else
        {
            Assert.ThrowsAny<Exception>(() => fixture.Service.Start(operation, "initial-configuration", request, DateTimeOffset.UtcNow));
            Assert.Equal(0, fixture.Inbox.PendingCount);
        }
        Assert.Equal(before, File.ReadAllBytes(source.Configuration));
    }

    [Theory(DisplayName = "Sealed Ordinary Retry Input Retains Progress Independently Of Long Term Synchronization")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RetainsSealedProgress(bool autoSync)
    {
        await using var fixture = new RunWriterFixture(autoSync: autoSync);
        await fixture.StartAsync();
        var stopped = await fixture.StopAsync(); fixture.Seal(stopped);
        foreach (var resource in fixture.Session.Resources)
        {
            var plan = fixture.Prepared.Use(items => items.Single(item => item.Address == resource.Address).Plan);
            var source = new RunStateCodec(fixture.Base.Source.Instance, new(), fixture.Base.Source.Blobs).Read(plan.RunState!);
            var input = RetryConfigurationInput.Read(fixture.Base.Source.Instance, fixture.Base.Source.Database, new(), resource, source);
            Assert.Contains("\"count\":41", fixture.Base.Read(input.Tree));
            var selected = new ConfigurationTaskTransformation(fixture.Base.Source.Blobs).Select(resource.OperationId, source.Source, source.Tasks, inputTree: input.Tree);
            Assert.Contains("\"count\":41", fixture.Base.Read(selected));
            Assert.Contains("\"one\":true", fixture.Base.Read(selected));
            Assert.Contains("\"count\":17", fixture.Base.Read(new ConfigurationRevisionStore(fixture.Base.Source.Instance,
                fixture.Base.Source.Database, new()).Read(resource.Address)!.Tree));
        }
        fixture.Restore(); fixture.Base.VerifyOriginal();
    }
}
