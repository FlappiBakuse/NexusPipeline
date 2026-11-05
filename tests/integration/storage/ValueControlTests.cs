using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ValueControlTests
{
    [Fact(DisplayName = "Generic Structured Reads Use Verified Saved Bytes And Reject Stale Resource Identity Without Mutation")]
    public void ReadsSavedDocument()
    {
        using var fixture = new ExecutionAdmissionFixture(); var source = fixture.Source;
        var service = new ValueService(source.Instance, source.Database, new(), source.Lease, fixture.Resources);
        var resource = Assert.Single(service.Read(source.BindingA));
        var file = Assert.Single(service.ReadFiles(source.BindingA, resource.ResourceId, resource.Revision).Items);
        var before = fixture.Counts();
        var document = service.ReadDocument(source.BindingA, resource.ResourceId, resource.Revision, "", file.Sha256, ConfigurationSyntax.Json);
        Assert.True(document.Value.GetProperty("fixture").GetBoolean());
        Assert.Equal(file.Sha256, document.SourceSha256);
        Assert.Equal(resource.Revision, document.Revision);
        Assert.Throws<ConfigurationException>(() => service.ReadDocument(source.BindingA, resource.ResourceId,
            resource.Revision + 1, "", file.Sha256, ConfigurationSyntax.Json));
        Assert.Throws<ConfigurationException>(() => service.ReadDocument(source.BindingA, resource.ResourceId,
            resource.Revision, "", new string('0', 64), ConfigurationSyntax.Json));
        Assert.Throws<ConfigurationException>(() => service.ReadDocument(source.BindingA, resource.ResourceId,
            resource.Revision, "", file.Sha256, ConfigurationSyntax.Opaque));
        Assert.Equal(before, fixture.Counts());
    }

    [Fact(DisplayName = "Real Generic Saved Value Edits Keep Live Bytes And Replay The Original Operation After Later Binding Changes")]
    public void ReplaysSavedEdits()
    {
        using var fixture = new ExecutionAdmissionFixture(); var source = fixture.Source;
        var service = new ValueService(source.Instance, source.Database, new(), source.Lease, fixture.Resources);
        var resource = Assert.Single(service.Read(source.BindingA));
        var file = Assert.Single(service.ReadFiles(source.BindingA, resource.ResourceId, resource.Revision).Items); var live = File.ReadAllBytes(source.Configuration);
        var operation = Guid.NewGuid();
        var request = Bytes(operation, resource.ResourceId, file.Sha256, true, false);
        var before = fixture.Counts();
        Assert.True(service.Preview("generic-control-fixture", source.BindingA, 1, "", ConfigurationSyntax.Json, request).HasChanges);
        Assert.Equal(before, fixture.Counts());
        var result = service.Apply("generic-control-fixture", source.BindingA, 1, "", ConfigurationSyntax.Json, request, DateTimeOffset.UtcNow);
        Assert.Equal(2, result.Saved.Revision); Assert.Equal(live, File.ReadAllBytes(source.Configuration));
        var changed = Assert.Single(result.Saved.Tree.Entries);
        using (var content = new ImmutableBlobStore(source.Instance, new()).OpenVerified(new($"blobs/content/{changed.Sha256}.bin", changed.SizeBytes, changed.Sha256!)))
        using (var json = JsonDocument.Parse(content)) Assert.False(json.RootElement.GetProperty("fixture").GetBoolean());
        var catalog = source.Definitions.Read(); var binding = catalog.Bindings.Single(item => item.Id == source.BindingA);
        var changes = new AutomationChanges(catalog.Revision, [], [], [], [binding with { Priority = binding.Priority + 1 }], [], [], []);
        source.Definitions.Commit(NexusPipeline.Platform.Storage.Automation.AutomationDefinitionStore.Identity(
            Guid.NewGuid(), "generic-control-fixture", changes), changes, DateTimeOffset.UtcNow);
        var replay = service.Apply("generic-control-fixture", source.BindingA, 1, "", ConfigurationSyntax.Json, request, DateTimeOffset.UtcNow);
        Assert.False(replay.IsNew); Assert.Equal(result.Saved.Revision, replay.Saved.Revision);
        Assert.Equal(result.Saved.Tree.Entries.ToArray(), replay.Saved.Tree.Entries.ToArray());
        Assert.Equal(live, File.ReadAllBytes(source.Configuration));
        Assert.Throws<OperationConflictException>(() => service.Apply("foreign", source.BindingA, 1, "",
            ConfigurationSyntax.Json, request, DateTimeOffset.UtcNow));
        Assert.Equal(2, Assert.Single(service.Read(source.BindingA)).Revision);
    }

    [Fact(DisplayName = "A Real Definition Mutation And An Undeclared Generic Resource Cannot Borrow Value Write Authority")]
    public void RejectsUndeclaredResources()
    {
        using var fixture = new ExecutionAdmissionFixture(); var source = fixture.Source;
        var service = new ValueService(source.Instance, source.Database, new(), source.Lease, fixture.Resources);
        var resource = Assert.Single(service.Read(source.BindingA)); var file = Assert.Single(service.ReadFiles(source.BindingA, resource.ResourceId, resource.Revision).Items);
        var bytes = Bytes(Guid.NewGuid(), resource.ResourceId, file.Sha256, true, false);
        var before = fixture.Counts(); var live = File.ReadAllBytes(source.Configuration);
        using (var mutation = fixture.Resources.TryAcquire(Guid.NewGuid(),
            [new("automation:definitions", NexusPipeline.Core.Maintenance.ResourceClaimMode.Mutation)]).Lease!)
            Assert.Equal("configuration_resource_conflict", Assert.Throws<ConfigurationException>(() => service.Apply(
                "generic-control-fixture", source.BindingA, 1, "", ConfigurationSyntax.Json, bytes, DateTimeOffset.UtcNow)).Message);
        Assert.Throws<ConfigurationException>(() => service.Apply("generic-control-fixture", source.OtherBinding, 1, "",
            ConfigurationSyntax.Json, bytes, DateTimeOffset.UtcNow));
        Assert.Equal(before, fixture.Counts()); Assert.Equal(live, File.ReadAllBytes(source.Configuration));
        Assert.Equal(1, Assert.Single(service.Read(source.BindingA)).Revision);
    }

    private static byte[] Bytes(Guid operation, string resource, string source, bool expected, bool value) =>
        JsonSerializer.SerializeToUtf8Bytes(new { format = "nexus.config.patch", version = 1,
            operationId = operation, resourceId = resource, sourceSha256 = source, expectedRevision = 1,
            edits = new[] { new { selector = new[] { "fixture" }, expected, value } } });
}
