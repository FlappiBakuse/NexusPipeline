using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility.State;
using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class SecretDocumentTests
{
    private const string Extension = "fixture-plugin";
    private const string Document = "tasks";

    [Fact(DisplayName = "A Real Accepted Run Can Complete Its Declared Atomic Credential Rotation During Host Drain While New Unaccepted Writes Remain Blocked")]
    public async Task ContinuesAcceptedRotation()
    {
        using var fixture = new ExtensionCatalogFixture(); var operation = Guid.NewGuid();
        using var root = fixture.Resources.TryAcquire(operation, AcceptedManualWork.Claims("fixture-plugin")).Lease!;
        using var activity = root.AcceptRun(fixture.Resources, operation, Guid.NewGuid()); root.Dispose();
        var session = Guid.NewGuid(); var close = fixture.Lifecycle.RequestClose(session, Guid.NewGuid());
        Assert.Null(close.Permit); Assert.Equal(HostLifecyclePhase.Draining, fixture.Lifecycle.Phase);
        var service = new SecretDocumentService(fixture.Instance, fixture.Database, fixture.Lease, fixture.Resources, fixture.Index, new());
        var subject = Guid.NewGuid(); using var changes = new Edits(subject, 0, "synthetic-rotation");
        var write = Guid.NewGuid();
        var json = Json(1);
        var rejection = await Assert.ThrowsAsync<IOException>(() => service.WriteAsync(write, "extension:fixture-plugin", Extension, Document, 0,
            json, changes.Values, () => { }, default).AsTask());
        Assert.Equal("secret_document_resource_unavailable", rejection.Message);
        Assert.Null(ReadPublication(fixture, write));
        Assert.Null(ReadDocument(fixture, Document));
        var fields = new SecretFieldStore(fixture.Instance, fixture.Database);
        AssertUnconfigured(fields, changes.Values, 0);
        var committed = await service.WriteAsync(write, "extension:fixture-plugin", Extension, Document, 0,
            json, changes.Values, () => { }, default, activity);
        Assert.Equal(ExpectedCommit(write, 1, json), committed);
        AssertDocument(fixture, Document, 1, json);
        AssertSecrets(fields, changes.Values, 1, "synthetic-rotation");
        activity.Dispose();
        Assert.NotNull(fixture.Lifecycle.RequestClose(session, Guid.NewGuid()).Permit);
    }

    [Fact(DisplayName = "Six Actual Dpapi Candidates And Their Ordinary Document Become Visible In One Commit With Stable Replay And Explicit Retirement")]
    public async Task PublishesCompleteBatch()
    {
        using var fixture = new ExtensionCatalogFixture();
        var subject = Guid.NewGuid();
        var fields = new SecretFieldStore(fixture.Instance, fixture.Database);
        var service = new SecretDocumentService(fixture.Instance, fixture.Database, fixture.Lease, fixture.Resources, fixture.Index, new());
        var firstJson = Json(1);
        using var first = new Edits(subject, 0, "first");
        await service.WriteAsync(Guid.NewGuid(), "editor:one", Extension, Document, 0, firstJson, first.Values, () => { }, default);
        using var second = new Edits(subject, 1, "second"); var operation = Guid.NewGuid();
        var observed = false;
        var observer = new ObservePreparedSecret(() =>
        {
            observed = true;
            AssertDocument(fixture, Document, 1, firstJson);
            AssertSecrets(fields, first.Values, 1, "first");
            Assert.Null(service.ReadCommit(operation, Extension, "editor:one"));
        });
        service = new(fixture.Instance, fixture.Database, fixture.Lease, fixture.Resources, fixture.Index, new(), observer);
        var secondJson = Json(2);
        var committed = await service.WriteAsync(operation, "editor:one", Extension, Document, 1, secondJson, second.Values, () => { }, default);
        Assert.True(observed);
        Assert.Equal(ExpectedCommit(operation, 2, secondJson), committed);
        AssertDocument(fixture, Document, 2, secondJson);
        var published = AssertSecrets(fields, second.Values, 2, "second");
        service = new(fixture.Instance, fixture.Database, fixture.Lease, fixture.Resources, fixture.Index, new());
        using var wrong = new Edits(subject, 1, "different");
        await Assert.ThrowsAsync<OperationConflictException>(() => service.WriteAsync(operation, "editor:one", Extension, Document, 1,
            secondJson, wrong.Values, () => { }, default).AsTask());
        var latest = Json(3);
        _ = await service.WriteAsync(Guid.NewGuid(), "editor:one", Extension, Document, 2, latest, [], () => { }, default);
        var replay = await service.WriteAsync(operation, "editor:one", Extension, Document, 1, secondJson, second.Values, () => { }, default);
        Assert.Equal(committed with { IsNew = false }, replay);
        Assert.Equal(replay, service.ReadCommit(operation, Extension, "editor:one"));
        Assert.Null(service.ReadCommit(operation, Extension, "other:editor"));
        AssertDocument(fixture, Document, 3, latest);
        var afterReplay = AssertSecrets(fields, second.Values, 2, "second");
        foreach (var field in published.Keys) Assert.Equal(published[field], afterReplay[field]);
        var clears = second.Values.Select(edit => new SecretDocumentEdit(subject, edit.Field, 2, SecretDisposition.Clear, null)).ToArray();
        var cleared = Json(4);
        _ = await service.WriteAsync(Guid.NewGuid(), "editor:one", Extension, Document, 3, cleared, clears, () => { }, default);
        AssertDocument(fixture, Document, 4, cleared);
        AssertUnconfigured(fields, clears, 3);
        new SecretDocumentInventory(fixture.Instance, fixture.Database, new()).Verify();
        SecretFieldStore.VerifyInventory(fixture.Database);
    }

    [Fact(DisplayName = "A Stale Ordinary Document Revision Does Not Publish Any Credential Candidate Or Consume Its Operation Identity")]
    public async Task RejectsStaleDocument()
    {
        using var fixture = new ExtensionCatalogFixture(); var subject = Guid.NewGuid();
        var service = new SecretDocumentService(fixture.Instance, fixture.Database, fixture.Lease, fixture.Resources, fixture.Index, new());
        var first = Json(1); var next = Json(2);
        _ = await service.WriteAsync(Guid.NewGuid(), "editor", Extension, Document, 0, first, [], () => { }, default);
        using var edits = new Edits(subject, 0, "synthetic"); var operation = Guid.NewGuid();
        var conflict = await Assert.ThrowsAsync<InvalidOperationException>(() => service.WriteAsync(operation, "editor", Extension, Document, 0,
            next, edits.Values, () => { }, default).AsTask());
        Assert.Equal("extension_document_revision_conflict", conflict.Message);
        var fields = new SecretFieldStore(fixture.Instance, fixture.Database);
        AssertUnconfigured(fields, edits.Values, 0);
        AssertDocument(fixture, Document, 1, first);
        Assert.Null(ReadPublication(fixture, operation));
        var accepted = await service.WriteAsync(operation, "editor", Extension, Document, 1, next, edits.Values, () => { }, default);
        Assert.Equal(ExpectedCommit(operation, 2, next), accepted);
        AssertDocument(fixture, Document, 2, next);
        AssertSecrets(fields, edits.Values, 1, "synthetic");
    }

    private static byte[] Json(int revision) => JsonSerializer.SerializeToUtf8Bytes(new { formatVersion = 1, revision, tasks = Array.Empty<object>() });
    internal static DocumentCommit ExpectedCommit(Guid operation, long revision, byte[] json) =>
        new(operation, revision, Convert.ToHexStringLower(SHA256.HashData(json)), true);

    internal static ExtensionDocument? ReadDocument(ExtensionCatalogFixture fixture, string name)
    {
        using var connection = DatabaseConnections.Open(fixture.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Extensions);
        return ExtensionDocumentRows.Read(connection, null, Extension, name);
    }

    internal static void AssertDocument(ExtensionCatalogFixture fixture, string name, long revision, byte[] json)
    {
        var document = ReadDocument(fixture, name);
        Assert.NotNull(document);
        Assert.Equal(revision, document.Revision);
        Assert.Equal(json, document.Json.ToArray());
    }

    internal static BoundSecretDocument? ReadPublication(ExtensionCatalogFixture fixture, Guid operation)
    {
        using var connection = DatabaseConnections.Open(fixture.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.System);
        var reference = SecretDocumentRows.Find(connection, null, operation);
        return reference is null ? null : new SecretDocumentFormat(fixture.Instance, fixture.Contracts).Read(reference);
    }

    private static Dictionary<string, SecretFieldStatus> AssertSecrets(SecretFieldStore fields, SecretDocumentEdit[] edits, long revision, string prefix)
    {
        var states = new Dictionary<string, SecretFieldStatus>(StringComparer.Ordinal);
        foreach (var edit in edits)
        {
            var key = new SecretFieldKey(Extension, edit.SubjectId, edit.Field);
            var state = fields.GetStateAsync(key, default).GetAwaiter().GetResult();
            Assert.Equal(revision, state.Revision);
            Assert.True(state.IsConfigured && state.IsReadable);
            using var value = fields.ReadAsync(key, default).GetAwaiter().GetResult();
            var expected = prefix + "-" + edit.Field;
            value.Use(text => Assert.True(text.SequenceEqual(expected)));
            states.Add(edit.Field, state);
        }
        return states;
    }

    private static void AssertUnconfigured(SecretFieldStore fields, SecretDocumentEdit[] edits, long revision)
    {
        foreach (var edit in edits)
            Assert.Equal(new SecretFieldStatus(revision, null, false, false),
                fields.GetStateAsync(new(Extension, edit.SubjectId, edit.Field), default).GetAwaiter().GetResult());
    }

    internal sealed class ObservePreparedSecret(Action check) : ISecretPreparationObserver
    {
        public void Reached(SecretPreparationPoint point, Guid journalId)
        {
            if (point == SecretPreparationPoint.CommittedJournal) check();
        }
    }
    private sealed class Edits : IDisposable
    {
        internal SecretDocumentEdit[] Values { get; }
        internal Edits(Guid subject, long revision, string prefix) => Values = new[] { "cn", "os", "skland", "skport", "kuro", "tajiduo" }
            .Select(field => new SecretDocumentEdit(subject, field, revision, SecretDisposition.Set, new SecretValue(prefix + "-" + field))).ToArray();
        public void Dispose() { foreach (var edit in Values) edit.Value?.Dispose(); }
    }
}
