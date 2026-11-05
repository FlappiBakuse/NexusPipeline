using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class SecretProjectTests
{
    private const string Field = "input-fixture";
    private const string PrivateValue = "PROTECTED_PROJECT_PRIVATE_SENTINEL";

    [Fact(DisplayName = "Actual DPAPI Project Selection And Protected Set Keep Clear Publish Together And Frozen Reads Reject Another Binding Or Revision")]
    public async Task PublishesAtomicChoice()
    {
        using var project = new ProjectSourceFixture(); var fixture = project.Catalog;
        var fields = new SecretFieldStore(fixture.Instance, fixture.Database);
        var key = new SecretFieldKey("fixture-plugin", project.Binding, Field);
        var observations = 0;
        var service = Service(fixture, new Observe(() =>
        {
            Assert.Null(project.Revisions.Read(ProjectChoiceStore.Address(project.Binding)));
            Assert.Equal(0, fields.GetStateAsync(key, default).Result.Revision); observations++;
        }));
        using var choice = Choice(project, 0); using var material = new SecretValue(PrivateValue);
        var operation = Guid.NewGuid();
        var changes = new[] { new SecretDocumentEdit(project.Binding, Field, 0, SecretDisposition.Set, material) };
        await service.WriteAsync(operation, "editor", "fixture-plugin", ProjectChoicePublication.Name(project.Binding), 0,
            choice.Bytes, changes, () => { }, default, projectChoice: choice.Publication);
        Assert.True(observations > 0);
        Assert.Equal(1, project.Revisions.Read(ProjectChoiceStore.Address(project.Binding))!.Revision);
        var state = await fields.GetStateAsync(key, default); Assert.True(state.IsReadable);
        var frozen = new SecretFieldReference(state.Revision, state.SecretId);
        using (var value = fields.ReadProjectInput(key, frozen)) value.Use(text => Assert.True(text.SequenceEqual(PrivateValue)));
        Assert.Throws<InvalidOperationException>(() => fields.ReadProjectInput(new("fixture-plugin", Guid.NewGuid(), Field), frozen));
        Assert.Throws<InvalidOperationException>(() => fields.ReadProjectInput(new("other-plugin", project.Binding, Field), frozen));
        var status = Assert.Single(ProjectInputStates.Read(fixture.Instance, fixture.Database, fixture.Contracts, "fixture-plugin", project.Binding));
        Assert.Equal(Field, status.Key); Assert.Equal(1, status.Revision); Assert.True(status.IsConfigured);
        service = Service(fixture);
        await new ProjectChoiceStore(fixture.Instance, fixture.Database, fixture.Contracts).VerifyReplayAsync(service, operation,
            [new(new(Field, 0, SecretDisposition.Set), material)], () => { }, default);
        using var wrong = new SecretValue("a-different-private-value");
        await Assert.ThrowsAsync<OperationConflictException>(() => new ProjectChoiceStore(fixture.Instance, fixture.Database, fixture.Contracts)
            .VerifyReplayAsync(service, operation, [new(new(Field, 0, SecretDisposition.Set), wrong)], () => { }, default).AsTask());
        using var second = Choice(project, 1);
        await service.WriteAsync(Guid.NewGuid(), "editor", "fixture-plugin", ProjectChoicePublication.Name(project.Binding), 1,
            second.Bytes, [new(project.Binding, Field, 1, SecretDisposition.Keep, null)], () => { }, default, projectChoice: second.Publication);
        Assert.Equal(frozen, new SecretFieldReference((await fields.GetStateAsync(key, default)).Revision, state.SecretId));
        using var third = Choice(project, 2);
        await service.WriteAsync(Guid.NewGuid(), "editor", "fixture-plugin", ProjectChoicePublication.Name(project.Binding), 2,
            third.Bytes, [new(project.Binding, Field, 1, SecretDisposition.Clear, null)], () => { }, default, projectChoice: third.Publication);
        Assert.Equal(3, project.Revisions.Read(ProjectChoiceStore.Address(project.Binding))!.Revision);
        Assert.False((await fields.GetStateAsync(key, default)).IsConfigured);
        Assert.Throws<InvalidOperationException>(() => fields.ReadProjectInput(key, frozen));
        new SecretDocumentInventory(fixture.Instance, fixture.Database, fixture.Contracts).Verify();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(fixture.Instance.RuntimeRoot, "blobs"), "*", SearchOption.AllDirectories))
            Assert.False(File.ReadAllBytes(path).AsSpan().IndexOf(Encoding.UTF8.GetBytes(PrivateValue)) >= 0);
    }

    [Fact(DisplayName = "A Stale Project CAS Is Rejected Before Accepting A Protected Candidate Or Its Operation")]
    public async Task RejectsStaleChoice()
    {
        using var project = new ProjectSourceFixture(); var fixture = project.Catalog;
        using var existing = Choice(project, 0);
        project.Revisions.Commit(ConfigurationRevisionStore.Identity(Guid.NewGuid(), "prior", existing.Publication.Change),
            existing.Prepared, DateTimeOffset.UtcNow);
        using var material = new SecretValue(PrivateValue); var operation = Guid.NewGuid();
        await Assert.ThrowsAsync<ConfigurationException>(() => Service(fixture).WriteAsync(operation, "editor", "fixture-plugin",
            ProjectChoicePublication.Name(project.Binding), 0, existing.Bytes,
            [new(project.Binding, Field, 0, SecretDisposition.Set, material)], () => { }, default, projectChoice: existing.Publication).AsTask());
        using var connection = DatabaseConnections.Open(fixture.Database.DatabasePath, readOnly: true);
        Assert.Null(SecretDocumentRows.Find(connection, null, operation));
        Assert.Equal(0, (await new SecretFieldStore(fixture.Instance, fixture.Database).GetStateAsync(new("fixture-plugin", project.Binding, Field), default)).Revision);
        Assert.Equal(1, project.Revisions.Read(ProjectChoiceStore.Address(project.Binding))!.Revision);
    }

    [Fact(DisplayName = "Revocation After Accepted Preparation Leaves Both Publications Unchanged And Never Replays A New Protected Write")]
    public async Task PreservesUnknownChoice()
    {
        using var project = new ProjectSourceFixture(); var fixture = project.Catalog; var valid = true;
        var service = Service(fixture, new Observe(() => valid = false));
        using var choice = Choice(project, 0); using var material = new SecretValue(PrivateValue); var operation = Guid.NewGuid();
        var changes = new[] { new SecretDocumentEdit(project.Binding, Field, 0, SecretDisposition.Set, material) };
        void Current() { if (!valid) throw new UnauthorizedAccessException("fixture_revoked"); }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.WriteAsync(operation, "editor", "fixture-plugin",
            ProjectChoicePublication.Name(project.Binding), 0, choice.Bytes, changes, Current, default, projectChoice: choice.Publication).AsTask());
        Assert.Null(project.Revisions.Read(ProjectChoiceStore.Address(project.Binding)));
        Assert.Null(service.ReadCommit(operation, "fixture-plugin", "editor"));
        var fields = new SecretFieldStore(fixture.Instance, fixture.Database);
        Assert.Equal(0, (await fields.GetStateAsync(new("fixture-plugin", project.Binding, Field), default)).Revision);
        var payloads = Directory.EnumerateFiles(Path.Combine(fixture.Instance.RuntimeRoot, "secrets/payloads")).Order(StringComparer.Ordinal).ToArray();
        valid = true;
        var error = await Assert.ThrowsAsync<IOException>(() => service.WriteAsync(operation, "editor", "fixture-plugin",
            ProjectChoicePublication.Name(project.Binding), 0, choice.Bytes, changes, Current, default, projectChoice: choice.Publication).AsTask());
        Assert.Equal("secret_document_publication_pending", error.Message);
        Assert.Equal(payloads, Directory.EnumerateFiles(Path.Combine(fixture.Instance.RuntimeRoot, "secrets/payloads")).Order(StringComparer.Ordinal).ToArray());
        Assert.Null(project.Revisions.Read(ProjectChoiceStore.Address(project.Binding)));
        Assert.Null(service.ReadCommit(operation, "fixture-plugin", "another-editor"));
    }

    private static SecretDocumentService Service(ExtensionCatalogFixture fixture, ISecretPreparationObserver? observer = null) =>
        new(fixture.Instance, fixture.Database, fixture.Lease, fixture.Resources, fixture.Index, fixture.Contracts, observer);

    private static PreparedChoice Choice(ProjectSourceFixture project, long revision)
    {
        var fixture = project.Catalog; var bytes = JsonSerializer.SerializeToUtf8Bytes(new { selection = revision + 1, protectedField = Field });
        var content = new ImmutableBlobStore(fixture.Instance, fixture.Contracts).PersistContent(bytes, default);
        var change = new ConfigurationRevisionChange(ProjectChoiceStore.Address(project.Binding), revision, 1,
            new(1, new string('a', 64), null, false), new(FileObjectKind.File, [new("", FileObjectKind.File, content.SizeBytes, content.Sha256)]),
            ConfigurationUpdateKind.Manual) { SourceRequestDigest = new string('b', 64) };
        var prepared = project.Revisions.Prepare(change);
        return new(bytes, prepared, prepared.Use((frozen, manifest) => new ProjectChoicePublication(frozen, manifest, DateTimeOffset.UtcNow)));
    }
    private sealed record PreparedChoice(byte[] Bytes, PreparedConfigurationRevision Prepared, ProjectChoicePublication Publication) : IDisposable
    { public void Dispose() => Prepared.Dispose(); }
    private sealed class Observe(Action action) : ISecretPreparationObserver
    { public void Reached(SecretPreparationPoint point, Guid journalId) => action(); }
}
