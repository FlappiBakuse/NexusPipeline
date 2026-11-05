using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class SecretDocumentTests
{
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
        await Assert.ThrowsAsync<IOException>(() => service.WriteAsync(Guid.NewGuid(), "extension:fixture-plugin", "fixture-plugin", "tasks", 0,
            Json(1), changes.Values, () => { }, default).AsTask());
        var committed = await service.WriteAsync(Guid.NewGuid(), "extension:fixture-plugin", "fixture-plugin", "tasks", 0,
            Json(1), changes.Values, () => { }, default, activity);
        Assert.Equal(1, committed.Revision);
        foreach (var edit in changes.Values) Assert.True(ReadState(fixture, subject, edit.Field).IsConfigured);
        activity.Dispose();
        Assert.NotNull(fixture.Lifecycle.RequestClose(session, Guid.NewGuid()).Permit);
    }

    [Fact(DisplayName = "Six Actual Dpapi Candidates And Their Ordinary Document Become Visible In One Commit With Stable Replay And Explicit Retirement")]
    public async Task PublishesCompleteBatch()
    {
        using var fixture = new ExtensionCatalogFixture();
        var subject = Guid.NewGuid();
        var service = new SecretDocumentService(fixture.Instance, fixture.Database, fixture.Lease, fixture.Resources, fixture.Index, new());
        using var first = new Edits(subject, 0, "first");
        var initial = await service.WriteAsync(Guid.NewGuid(), "editor:one", "fixture-plugin", "tasks", 0, Json(1), first.Values, () => { }, default);
        Assert.Equal(1, initial.Revision);
        var observations = 0;
        var observer = new Observe(() =>
        {
            Assert.Equal(1, ReadDocument(fixture).Revision);
            foreach (var edit in first.Values)
            {
                var status = ReadState(fixture, subject, edit.Field);
                Assert.Equal(1, status.Revision);
                using var value = new SecretFieldStore(fixture.Instance, fixture.Database).ReadAsync(new("fixture-plugin", subject, edit.Field), default).GetAwaiter().GetResult();
                value.Use(text => Assert.StartsWith("first-", text.ToString(), StringComparison.Ordinal));
            }
            observations++;
        });
        service = new(fixture.Instance, fixture.Database, fixture.Lease, fixture.Resources, fixture.Index, new(), observer);
        using var second = new Edits(subject, 1, "second"); var operation = Guid.NewGuid();
        var committed = await service.WriteAsync(operation, "editor:one", "fixture-plugin", "tasks", 1, Json(2), second.Values, () => { }, default);
        Assert.True(observations >= 6); Assert.Equal(2, committed.Revision);
        Assert.Equal(2, ReadDocument(fixture).Revision);
        foreach (var edit in second.Values) Assert.Equal(2, ReadState(fixture, subject, edit.Field).Revision);
        service = new(fixture.Instance, fixture.Database, fixture.Lease, fixture.Resources, fixture.Index, new());
        var replay = await service.WriteAsync(operation, "editor:one", "fixture-plugin", "tasks", 1, Json(2), second.Values, () => { }, default);
        Assert.False(replay.IsNew); Assert.Equal(2, replay.Revision);
        using var wrong = new Edits(subject, 1, "different");
        await Assert.ThrowsAsync<OperationConflictException>(() => service.WriteAsync(operation, "editor:one", "fixture-plugin", "tasks", 1,
            Json(2), wrong.Values, () => { }, default).AsTask());
        _ = await service.WriteAsync(Guid.NewGuid(), "editor:one", "fixture-plugin", "tasks", 2, Json(3), [], () => { }, default);
        replay = await service.WriteAsync(operation, "editor:one", "fixture-plugin", "tasks", 1, Json(2), second.Values, () => { }, default);
        Assert.Equal(2, replay.Revision); Assert.Equal(3, ReadDocument(fixture).Revision);
        Assert.Null(service.ReadCommit(operation, "fixture-plugin", "other:editor"));
        var clears = second.Values.Select(edit => new SecretDocumentEdit(subject, edit.Field, 2, SecretDisposition.Clear, null)).ToArray();
        _ = await service.WriteAsync(Guid.NewGuid(), "editor:one", "fixture-plugin", "tasks", 3, Json(4), clears, () => { }, default);
        foreach (var edit in clears)
        { var state = ReadState(fixture, subject, edit.Field); Assert.Equal(3, state.Revision); Assert.False(state.IsConfigured); }
        new SecretDocumentInventory(fixture.Instance, fixture.Database, new()).Verify();
        SecretFieldStore.VerifyInventory(fixture.Database);
    }

    [Fact(DisplayName = "A Stale Ordinary Document Revision Does Not Publish Any Credential Candidate Or Consume Its Operation Identity")]
    public async Task RejectsStaleDocument()
    {
        using var fixture = new ExtensionCatalogFixture(); var subject = Guid.NewGuid();
        var service = new SecretDocumentService(fixture.Instance, fixture.Database, fixture.Lease, fixture.Resources, fixture.Index, new());
        _ = await service.WriteAsync(Guid.NewGuid(), "editor", "fixture-plugin", "tasks", 0, Json(1), [], () => { }, default);
        using var edits = new Edits(subject, 0, "synthetic"); var operation = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.WriteAsync(operation, "editor", "fixture-plugin", "tasks", 0,
            Json(2), edits.Values, () => { }, default).AsTask());
        foreach (var edit in edits.Values) Assert.Equal(0, ReadState(fixture, subject, edit.Field).Revision);
        Assert.Null(service.ReadCommit(operation, "fixture-plugin", "editor"));
        var accepted = await service.WriteAsync(operation, "editor", "fixture-plugin", "tasks", 1, Json(2), edits.Values, () => { }, default);
        Assert.Equal(2, accepted.Revision); Assert.True(accepted.IsNew);
    }

    private static byte[] Json(int revision) => JsonSerializer.SerializeToUtf8Bytes(new { formatVersion = 1, revision, tasks = Array.Empty<object>() });
    private static Extensibility.State.ExtensionDocument ReadDocument(ExtensionCatalogFixture fixture)
    {
        using var connection = DatabaseConnections.Open(fixture.Database.DatabasePath, readOnly: true);
        return ExtensionDocumentRows.Read(connection, null, "fixture-plugin", "tasks")!;
    }
    private static SecretFieldStatus ReadState(ExtensionCatalogFixture fixture, Guid subject, string field) =>
        new SecretFieldStore(fixture.Instance, fixture.Database).GetStateAsync(new("fixture-plugin", subject, field), default).GetAwaiter().GetResult();
    private sealed class Observe(Action check) : ISecretPreparationObserver
    { public void Reached(SecretPreparationPoint point, Guid journalId) => check(); }
    private sealed class Edits : IDisposable
    {
        internal SecretDocumentEdit[] Values { get; }
        internal Edits(Guid subject, long revision, string prefix) => Values = new[] { "cn", "os", "skland", "skport", "kuro", "tajiduo" }
            .Select(field => new SecretDocumentEdit(subject, field, revision, SecretDisposition.Set, new SecretValue(prefix + "-" + field))).ToArray();
        public void Dispose() { foreach (var edit in Values) edit.Value?.Dispose(); }
    }
}
