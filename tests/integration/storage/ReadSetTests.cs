using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage.Configuration;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class ReadSetTests
{
    [Fact(DisplayName = "Public Missing Field Patches Materialize Two Fields Across Jsonc And Yaml In One Revision")]
    public void MaterializesMissingFields()
    {
        using var fixture = new PatchBatchFixture();
        var request = fixture.Request(Guid.NewGuid())[..2];
        for (var index = 0; index < request.Length; index++)
            request[index] = fixture.EditWire(request[index], "edits", new[]
            {
                new { selector = new[] { "TaskItems" }, expected = (object?)null, expectedExists = false, value = (object)new[] { new { name = "same", enabled = true }, new { name = "same", enabled = false } } },
                new { selector = new[] { "CurrentTasks" }, expected = (object?)null, expectedExists = false, value = (object)new[] { 0 } }
            });
        var stamp = Stamp(fixture);
        using var prepared = fixture.Service.Preview("fixture", request, [stamp]);
        var result = fixture.Service.Commit(prepared, DateTimeOffset.UtcNow);
        Assert.True(result.IsNew);
        var published = Assert.Single(result.Items).Saved;
        Assert.Equal(fixture.Main, published.Address); Assert.Equal(2, published.Revision);
        Assert.Equal(1, fixture.Base.Revisions.Read(fixture.Extra)!.Revision);
        foreach (var item in request)
        {
            var entry = Assert.Single(published.Tree.Entries, entry => entry.RelativePath == item.RelativePath);
            var bytes = File.ReadAllBytes(Path.Combine(fixture.Content, entry.Sha256 + ".bin"));
            using var document = new ConfigurationDocument(new(result.OperationId, published.Address.ResourceId, published.Revision, entry.Sha256!), item.Syntax,
                new([new(new([]), true)], [], []), bytes);
            var tasks = document.Read(new([new ConfigurationProperty("TaskItems")]));
            (string? Name, bool Enabled)[] expected = [("same", true), ("same", false)];
            Assert.Equal(expected, tasks.EnumerateArray().Select(task =>
                (task.GetProperty("name").GetString(), task.GetProperty("enabled").GetBoolean())));
            var selected = document.Read(new([new ConfigurationProperty("CurrentTasks")]));
            Assert.Equal(0, Assert.Single(selected.EnumerateArray()).GetInt32());
            Assert.Equal("fixture-private", document.Read(new([new ConfigurationProperty("secret")])).GetString());
        }
        Assert.Contains("// 中文 😀", fixture.SavedText(fixture.Main, "one.jsonc"), StringComparison.Ordinal);
        Assert.Contains("# 保留", fixture.SavedText(fixture.Main, "two.yaml"), StringComparison.Ordinal);
        fixture.VerifyOriginal();
    }

    [Fact(DisplayName = "A Patch Publishes Only Its Write Resource While Holding Read Dependencies And Freezing Caller Inputs")]
    public void HoldsReadDependencies()
    {
        using var fixture = new PatchBatchFixture();
        var readSet = new[] { Stamp(fixture) };
        using var prepared = fixture.Service.Preview("fixture", fixture.Request(Guid.NewGuid())[..2], readSet);
        readSet[0] = readSet[0] with { SourceSha256 = new('b', 64) };
        var before = ReadSnapshots(fixture);
        using (var holder = fixture.Resources.TryAcquire(Guid.NewGuid(),
            [new(ConfigurationRevisionService.ResourceKey(fixture.Extra), ResourceClaimMode.Mutation)]).Lease)
        {
            Assert.NotNull(holder);
            Assert.Equal("configuration_resource_conflict", Assert.Throws<ConfigurationException>(() =>
                fixture.Service.Commit(prepared, DateTimeOffset.UtcNow)).Message);
            AssertUnpublished(fixture, prepared, before);
        }
        var result = fixture.Service.Commit(prepared, DateTimeOffset.UtcNow);
        Assert.True(result.IsNew);
        var saved = Assert.Single(result.Items).Saved;
        Assert.Equal(fixture.Main, saved.Address); Assert.Equal(2, saved.Revision);
        AssertSnapshot(saved, fixture.Base.Revisions.Read(fixture.Main)!);
        AssertSnapshot(Assert.Single(before, item => item.Address == fixture.Extra), fixture.Base.Revisions.Read(fixture.Extra)!);
        fixture.VerifyOriginal();
    }

    [Theory(DisplayName = "Read Dependencies Compare The Exact Revision Contract And Original File Inside The Publishing Transaction")]
    [InlineData("sha")]
    [InlineData("revision")]
    [InlineData("contract")]
    [InlineData("path")]
    [InlineData("same-bytes")]
    public void RejectsChangedReads(string change)
    {
        using var fixture = new PatchBatchFixture();
        var stamp = Stamp(fixture);
        stamp = change switch
        {
            "sha" => stamp with { SourceSha256 = new('b', 64) },
            "revision" => stamp with { ExpectedRevision = 2 },
            "contract" => stamp with { Contract = stamp.Contract with { Revision = 2 } },
            "path" => stamp with { RelativePath = "missing.json" },
            _ => stamp
        };
        using var prepared = fixture.Service.Preview("fixture", fixture.Request(Guid.NewGuid())[..2], [stamp]);
        if (change == "same-bytes")
        {
            var current = fixture.Base.Revisions.Read(fixture.Extra)!;
            fixture.Base.Save(new([new(fixture.Extra, 1, 1, current.Contract, current.Tree, ConfigurationUpdateKind.Manual)]));
        }
        var before = ReadSnapshots(fixture);
        Assert.Equal("configuration_read_set_changed", Assert.Throws<ConfigurationException>(() => fixture.Service.Commit(prepared, DateTimeOffset.UtcNow)).Message);
        AssertUnpublished(fixture, prepared, before);
    }

    [Fact(DisplayName = "A Dependency Change At Sql Operation Acceptance Rolls Back Acceptance And Every Write")]
    public void ChecksAtomicReads()
    {
        using var fixture = new PatchBatchFixture();
        var current = fixture.Base.Revisions.Read(fixture.Extra)!;
        fixture.Base.Save(new([new(fixture.Extra, 1, 1, current.Contract, current.Tree, ConfigurationUpdateKind.Manual)]));
        using var prepared = fixture.Service.Preview("fixture", fixture.Request(Guid.NewGuid())[..2], [Stamp(fixture)]);
        var before = ReadSnapshots(fixture);
        using var writes = new NexusPipeline.Platform.Storage.Sqlite.DatabaseWriteScope(fixture.Base.Database.DatabasePath);
        var connection = NexusPipeline.Platform.Storage.Sqlite.DatabaseWriteScope.Borrow(fixture.Base.Database.DatabasePath)!;
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TEMP TRIGGER fixture_read_race AFTER INSERT ON main.operations WHEN NEW.operation_kind='configuration.patch-batch' " +
            "BEGIN UPDATE configuration_current SET revision=1 WHERE resource_id='extra'; END;";
        command.ExecuteNonQuery();
        try
        {
            Assert.Equal("configuration_read_set_changed", Assert.Throws<ConfigurationException>(() => fixture.Service.Commit(prepared, DateTimeOffset.UtcNow)).Message);
        }
        finally { command.CommandText = "DROP TRIGGER temp.fixture_read_race;"; command.ExecuteNonQuery(); }
        AssertUnpublished(fixture, prepared, before);
        var result = fixture.Service.Commit(prepared, DateTimeOffset.UtcNow);
        Assert.True(result.IsNew); Assert.Equal(prepared.OperationId, result.OperationId);
        var saved = Assert.Single(result.Items).Saved;
        Assert.Equal(fixture.Main, saved.Address); Assert.Equal(2, saved.Revision);
        AssertSnapshot(saved, fixture.Base.Revisions.Read(fixture.Main)!);
        AssertSnapshot(Assert.Single(before, item => item.Address == fixture.Extra), fixture.Base.Revisions.Read(fixture.Extra)!);
    }

    [Fact(DisplayName = "Exact Completed Replay Binds The Read Set Without Reopening Its Now Changed Dependency")]
    public void BindsReadReplay()
    {
        using var fixture = new PatchBatchFixture();
        var request = fixture.Request(Guid.NewGuid())[..2];
        var stamp = Stamp(fixture);
        var original = fixture.Service.Apply("fixture", request, DateTimeOffset.UtcNow, readSet: [stamp]);
        var current = fixture.Base.Revisions.Read(fixture.Extra)!;
        fixture.Base.Save(new([new(fixture.Extra, 1, 1, current.Contract, current.Tree, ConfigurationUpdateKind.Manual)]));
        var replay = fixture.Service.Apply("fixture", request, DateTimeOffset.UtcNow, readSet: [stamp]);
        Assert.False(replay.IsNew); Assert.Equal(original.OperationId, replay.OperationId);
        AssertSnapshot(Assert.Single(original.Items).Saved, Assert.Single(replay.Items).Saved);
        Assert.Throws<OperationConflictException>(() => fixture.Service.Apply("fixture", request, DateTimeOffset.UtcNow,
            readSet: [stamp with { ExpectedRevision = 2 }]));
        Assert.Throws<OperationConflictException>(() => fixture.Service.Apply("fixture", request, DateTimeOffset.UtcNow));
    }

    [Fact(DisplayName = "A Write Resource Can Also Be An Exact Read Dependency In The Same Preparation")]
    public void ReadsWriteResource()
    {
        using var fixture = new PatchBatchFixture();
        var current = fixture.Base.Revisions.Read(fixture.Main)!;
        var entry = current.Tree.Entries.Single(item => item.RelativePath == "one.jsonc");
        var stamp = new ConfigurationReadStamp(fixture.Main, current.Revision, 1, current.Contract, entry.RelativePath, entry.Sha256!);
        var result = fixture.Service.Apply("fixture", fixture.Request(Guid.NewGuid())[..2], DateTimeOffset.UtcNow, readSet: [stamp]);
        Assert.True(result.IsNew); Assert.Single(result.Items);
    }

    [Fact(DisplayName = "A Corrupt Read Dependency Blob Rejects Before The Patch Can Publish Or Accept An Operation")]
    public void RejectsCorruptBytes()
    {
        using var fixture = new PatchBatchFixture();
        var stamp = Stamp(fixture);
        using var prepared = fixture.Service.Preview("fixture", fixture.Request(Guid.NewGuid())[..2], [stamp]);
        var before = ReadSnapshots(fixture);
        var path = Path.Combine(fixture.Content, stamp.SourceSha256 + ".bin");
        File.WriteAllText(path, "corrupted-read-only-input");
        Assert.Throws<IOException>(() => fixture.Service.Commit(prepared, DateTimeOffset.UtcNow));
        AssertUnpublished(fixture, prepared, before);
        Assert.Equal("corrupted-read-only-input", File.ReadAllText(path));
    }

    [Fact(DisplayName = "The Read Dependency File And Parent Remain Physically Held Through Actual Sql Publication")]
    public void HoldsReadBytes()
    {
        using var fixture = new PatchBatchFixture();
        var stamp = Stamp(fixture); var operation = Guid.NewGuid();
        var path = Path.Combine(fixture.Content, stamp.SourceSha256 + ".bin");
        var observed = new HashSet<BatchPersistencePoint>();
        var observer = new ReadObserver((point, operationId) =>
        {
            Assert.Equal(operation, operationId);
            Assert.Throws<IOException>(() => File.Delete(path));
            Assert.Throws<IOException>(() => Directory.Move(fixture.Content, fixture.Content + "-moved"));
            observed.Add(point);
        });
        var service = new PatchBatchService(fixture.Base.Instance, fixture.Base.Database, new(), fixture.Lease, fixture.Resources, observer);
        var result = service.Apply("fixture", fixture.Request(operation)[..2], DateTimeOffset.UtcNow, readSet: [stamp]);
        Assert.True(result.IsNew);
        Assert.Contains(BatchPersistencePoint.OperationAccepted, observed);
        Assert.Contains(BatchPersistencePoint.TransactionCommitted, observed);
        using var released = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private static ConfigurationRevision[] ReadSnapshots(PatchBatchFixture fixture) =>
        [fixture.Base.Revisions.Read(fixture.Main)!, fixture.Base.Revisions.Read(fixture.Extra)!];

    private static void AssertUnpublished(PatchBatchFixture fixture, PreparedPatchBatch prepared, ConfigurationRevision[] before)
    {
        var (batch, operation) = prepared.Use((frozen, identity, _) => (frozen, identity));
        Assert.Null(fixture.Base.Batches.Lookup(operation, batch));
        foreach (var saved in before) AssertSnapshot(saved, fixture.Base.Revisions.Read(saved.Address)!);
        fixture.VerifyOriginal();
    }

    private static void AssertSnapshot(ConfigurationRevision expected, ConfigurationRevision actual)
    {
        Assert.Equal(expected.Address, actual.Address); Assert.Equal(expected.Revision, actual.Revision);
        Assert.Equal(expected.UserId, actual.UserId); Assert.Equal(expected.ScriptId, actual.ScriptId);
        Assert.Equal(expected.Contract, actual.Contract); Assert.Equal(expected.Tree.Shape, actual.Tree.Shape);
        Assert.Equal(expected.Tree.Entries, actual.Tree.Entries);
    }

    private static ConfigurationReadStamp Stamp(PatchBatchFixture fixture)
    {
        var current = fixture.Base.Revisions.Read(fixture.Extra)!;
        var entry = current.Tree.Entries.Single(item => item.RelativePath == "three.json");
        return new(fixture.Extra, current.Revision, 1, current.Contract, entry.RelativePath, entry.Sha256!);
    }

    private sealed class ReadObserver(Action<BatchPersistencePoint, Guid> check) : IBatchPersistenceObserver
    {
        public void Reached(BatchPersistencePoint point, Guid operationId, int resourceOrdinal)
        {
            if (point is BatchPersistencePoint.OperationAccepted or BatchPersistencePoint.TransactionCommitted) check(point, operationId);
        }
    }
}
