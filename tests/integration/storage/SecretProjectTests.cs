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
using static NexusPipeline.StorageTests.SecretDocumentTests;

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
        var name = ProjectChoicePublication.Name(project.Binding);
        var operation = Guid.NewGuid();
        var observed = false;
        var service = Service(fixture, new ObservePreparedSecret(() =>
        {
            observed = true;
            Assert.Null(project.Revisions.Read(ProjectChoiceStore.Address(project.Binding)));
            Assert.Null(ReadDocument(fixture, name));
            Assert.Equal(new SecretFieldStatus(0, null, false, false), fields.GetStateAsync(key, default).GetAwaiter().GetResult());
        }));
        using var choice = Choice(project, 0); using var material = new SecretValue(PrivateValue);
        var changes = new[] { new SecretDocumentEdit(project.Binding, Field, 0, SecretDisposition.Set, material) };
        var committed = await service.WriteAsync(operation, "editor", "fixture-plugin", name, 0,
            choice.Bytes, changes, () => { }, default, projectChoice: choice.Publication);
        Assert.Equal(ExpectedCommit(operation, 1, choice.Bytes), committed);
        AssertChoice(project, choice);
        AssertDocument(fixture, name, 1, choice.Bytes);
        Assert.True(observed);
        var state = await fields.GetStateAsync(key, default);
        Assert.Equal(1, state.Revision);
        var frozen = new SecretFieldReference(state.Revision, state.SecretId);
        using (var value = fields.ReadProjectInput(key, frozen)) value.Use(text => Assert.True(text.SequenceEqual(PrivateValue)));
        Assert.Equal("secret_revision_conflict", Assert.Throws<InvalidOperationException>(() =>
            fields.ReadProjectInput(new("fixture-plugin", Guid.NewGuid(), Field), frozen)).Message);
        Assert.Equal("secret_revision_conflict", Assert.Throws<InvalidOperationException>(() =>
            fields.ReadProjectInput(new("other-plugin", project.Binding, Field), frozen)).Message);
        var status = Assert.Single(ProjectInputStates.Read(fixture.Instance, fixture.Database, fixture.Contracts, "fixture-plugin", project.Binding));
        Assert.Equal(Field, status.Key); Assert.Equal(1, status.Revision); Assert.True(status.IsConfigured);
        service = Service(fixture);
        await new ProjectChoiceStore(fixture.Instance, fixture.Database, fixture.Contracts).VerifyReplayAsync(service, operation,
            [new(new(Field, 0, SecretDisposition.Set), material)], () => { }, default);
        using var wrong = new SecretValue("a-different-private-value");
        await Assert.ThrowsAsync<OperationConflictException>(() => new ProjectChoiceStore(fixture.Instance, fixture.Database, fixture.Contracts)
            .VerifyReplayAsync(service, operation, [new(new(Field, 0, SecretDisposition.Set), wrong)], () => { }, default).AsTask());
        using var second = Choice(project, 1);
        await service.WriteAsync(Guid.NewGuid(), "editor", "fixture-plugin", name, 1,
            second.Bytes, [new(project.Binding, Field, 1, SecretDisposition.Keep, null)], () => { }, default, projectChoice: second.Publication);
        AssertChoice(project, second);
        AssertDocument(fixture, name, 2, second.Bytes);
        Assert.Equal(state, await fields.GetStateAsync(key, default));
        using (var kept = fields.ReadProjectInput(key, frozen)) kept.Use(text => Assert.True(text.SequenceEqual(PrivateValue)));
        using var third = Choice(project, 2);
        await service.WriteAsync(Guid.NewGuid(), "editor", "fixture-plugin", name, 2,
            third.Bytes, [new(project.Binding, Field, 1, SecretDisposition.Clear, null)], () => { }, default, projectChoice: third.Publication);
        Assert.Equal(committed with { IsNew = false }, service.ReadCommit(operation, "fixture-plugin", "editor"));
        AssertChoice(project, third);
        AssertDocument(fixture, name, 3, third.Bytes);
        Assert.Equal(new SecretFieldStatus(2, null, false, false), await fields.GetStateAsync(key, default));
        Assert.Equal("secret_revision_conflict", Assert.Throws<InvalidOperationException>(() => fields.ReadProjectInput(key, frozen)).Message);
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
        var name = ProjectChoicePublication.Name(project.Binding);
        var conflict = await Assert.ThrowsAsync<ConfigurationException>(() => Service(fixture).WriteAsync(operation, "editor", "fixture-plugin", name,
            0, existing.Bytes, [new(project.Binding, Field, 0, SecretDisposition.Set, material)], () => { }, default, projectChoice: existing.Publication).AsTask());
        Assert.Equal("configuration_revision_conflict", conflict.Message);
        Assert.Null(ReadPublication(fixture, operation));
        Assert.Null(ReadDocument(fixture, name));
        var fields = new SecretFieldStore(fixture.Instance, fixture.Database);
        Assert.Equal(new SecretFieldStatus(0, null, false, false), await fields.GetStateAsync(new("fixture-plugin", project.Binding, Field), default));
        AssertChoice(project, existing);
    }

    [Fact(DisplayName = "Revocation After Accepted Preparation Leaves Both Publications Unchanged And Never Replays A New Protected Write")]
    public async Task PreservesUnknownChoice()
    {
        using var project = new ProjectSourceFixture(); var fixture = project.Catalog;
        var revoked = false;
        var retrying = false;
        var service = Service(fixture, new ObservePreparedSecret(() =>
        {
            Assert.False(retrying);
            revoked = true;
        }));
        using var choice = Choice(project, 0); using var material = new SecretValue(PrivateValue); var operation = Guid.NewGuid();
        var name = ProjectChoicePublication.Name(project.Binding);
        var changes = new[] { new SecretDocumentEdit(project.Binding, Field, 0, SecretDisposition.Set, material) };
        void Current() { if (revoked && !retrying) throw new UnauthorizedAccessException("fixture_revoked"); }
        Assert.Equal("fixture_revoked", (await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.WriteAsync(operation, "editor", "fixture-plugin",
            name, 0, choice.Bytes, changes, Current, default, projectChoice: choice.Publication).AsTask())).Message);
        Assert.Null(project.Revisions.Read(ProjectChoiceStore.Address(project.Binding)));
        Assert.Null(ReadDocument(fixture, name));
        Assert.Null(service.ReadCommit(operation, "fixture-plugin", "editor"));
        var fields = new SecretFieldStore(fixture.Instance, fixture.Database);
        var key = new SecretFieldKey("fixture-plugin", project.Binding, Field);
        var unpublished = new SecretFieldStatus(0, null, false, false);
        Assert.Equal(unpublished, await fields.GetStateAsync(key, default));
        var pending = ReadPublication(fixture, operation);
        Assert.NotNull(pending);
        retrying = true;
        var error = await Assert.ThrowsAsync<IOException>(() => service.WriteAsync(operation, "editor", "fixture-plugin",
            name, 0, choice.Bytes, changes, Current, default, projectChoice: choice.Publication).AsTask());
        Assert.Equal("secret_document_publication_pending", error.Message);
        Assert.Equal(pending.Reference, ReadPublication(fixture, operation)!.Reference);
        Assert.Equal(unpublished, await fields.GetStateAsync(key, default));
        Assert.Null(project.Revisions.Read(ProjectChoiceStore.Address(project.Binding)));
        Assert.Null(ReadDocument(fixture, name));
        Assert.Null(service.ReadCommit(operation, "fixture-plugin", "editor"));
    }

    private static void AssertChoice(ProjectSourceFixture project, PreparedChoice choice)
    {
        var expected = choice.Publication.Change;
        var actual = project.Revisions.Read(ProjectChoiceStore.Address(project.Binding));
        Assert.NotNull(actual);
        Assert.Equal(expected.Address, actual.Address); Assert.Equal(expected.ExpectedRevision + 1, actual.Revision);
        Assert.Equal(expected.Contract, actual.Contract); Assert.Equal(expected.Tree.Shape, actual.Tree.Shape);
        Assert.Equal(expected.Tree.Entries, actual.Tree.Entries);
        var file = Assert.Single(actual.Tree.Entries);
        var fixture = project.Catalog;
        using var stream = new ImmutableBlobStore(fixture.Instance, fixture.Contracts).OpenVerified(new($"blobs/content/{file.Sha256}.bin", file.SizeBytes, file.Sha256!));
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        Assert.Equal(choice.Bytes, bytes.ToArray());
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
}
