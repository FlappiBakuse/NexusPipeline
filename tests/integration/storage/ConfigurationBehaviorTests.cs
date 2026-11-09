using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ConfigurationBehaviorTests
{
    [Fact(DisplayName = "An Invalid First Start Leaves No Saved Configuration Or Receipt And Can Be Retried With Valid Bytes")]
    public void EstablishesInitialSnapshot()
    {
        using var fixture = new ExecutionAdmissionFixture(saved: false);
        var source = fixture.Source;
        const string subject = "initial-configuration";
        var operation = Guid.NewGuid();
        var request = source.Request(new BindingTarget(source.BindingA));
        var overview = new ConfigurationOverviewReader(source.Instance, source.Database, source.Lease, new());
        var resource = Assert.Single(overview.Read(source.BindingA).Resources);
        var address = new ConfigurationAddress(source.BindingA, resource.ResourceId);
        var revisions = new ConfigurationRevisionStore(source.Instance, source.Database, new());

        var invalid = "{invalid"u8.ToArray();
        File.WriteAllBytes(source.Configuration, invalid);
        Assert.Throws<ConfigurationException>(() => fixture.Service.Start(operation, subject, request, DateTimeOffset.UtcNow));
        Assert.Null(revisions.Read(address));
        var acceptance = new RunAttemptAcceptance(source.Instance, new(), source.Database);
        Assert.Null(acceptance.LookupStart(ExecutionStartIdentity.Create(operation, subject, request)));
        Assert.Equal(0, fixture.Inbox.PendingCount);
        Assert.Equal(invalid, File.ReadAllBytes(source.Configuration));

        var original = "{\n  \"fixture\": true\n}\n"u8.ToArray();
        File.WriteAllBytes(source.Configuration, original);
        fixture.Service.Start(operation, subject, request, DateTimeOffset.UtcNow);
        using var accepted = fixture.Take(operation);
        var saved = Assert.IsType<ConfigurationRevision>(revisions.Read(address));
        Assert.Equal(1, saved.Revision);
        var entry = Assert.Single(saved.Tree.Entries);
        using var stream = new ImmutableBlobStore(source.Instance, new())
            .OpenVerified(new($"blobs/content/{entry.Sha256}.bin", entry.SizeBytes, entry.Sha256!));
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        Assert.Equal(original, bytes.ToArray());
        Assert.Equal(original, File.ReadAllBytes(source.Configuration));
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
