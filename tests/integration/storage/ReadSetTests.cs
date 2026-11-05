using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage.Configuration;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class ReadSetTests
{
    [Theory(DisplayName = "Public Missing Field Patches Materialize Two Fields Across Jsonc And Yaml In One Revision With Atomic Read Dependencies")]
    [InlineData(false)]
    [InlineData(true)]
    public void MaterializesMissingFields(bool changed)
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
        if (changed)
        {
            var current = fixture.Base.Revisions.Read(fixture.Extra)!;
            fixture.Base.Save(new([new(fixture.Extra, 1, 1, current.Contract, current.Tree, ConfigurationUpdateKind.Manual)]));
            var operations = fixture.Base.Count("operations");
            Assert.Throws<ConfigurationException>(() => fixture.Service.Commit(prepared, DateTimeOffset.UtcNow));
            Assert.Equal(operations, fixture.Base.Count("operations"));
            Assert.Equal(1, fixture.Base.Revisions.Read(fixture.Main)!.Revision);
            return;
        }
        var result = fixture.Service.Commit(prepared, DateTimeOffset.UtcNow);
        Assert.True(result.IsNew); Assert.Single(result.Items); Assert.Equal(2, result.Items[0].Saved.Revision);
        Assert.Equal(1, fixture.Base.Revisions.Read(fixture.Extra)!.Revision);
        foreach (var item in request)
        {
            var saved = fixture.SavedText(item.Address, item.RelativePath);
            Assert.Contains("TaskItems", saved, StringComparison.Ordinal); Assert.Contains("CurrentTasks", saved, StringComparison.Ordinal);
            Assert.Contains("fixture-private", saved, StringComparison.Ordinal);
        }
        Assert.Contains("// 中文 😀", fixture.SavedText(fixture.Main, "one.jsonc"), StringComparison.Ordinal);
        Assert.Contains("# 保留", fixture.SavedText(fixture.Main, "two.yaml"), StringComparison.Ordinal);
        Assert.False(fixture.Service.Apply("fixture", request, DateTimeOffset.UtcNow, readSet: [stamp]).IsNew);
        fixture.VerifyOriginal();
    }

    [Fact(DisplayName = "A Patch Publishes Only Its Write Resource While Holding Read Dependencies And Freezing Caller Inputs")]
    public void HoldsReadDependencies()
    {
        using var fixture = new PatchBatchFixture();
        var readSet = new[] { Stamp(fixture) };
        using var prepared = fixture.Service.Preview("fixture", fixture.Request(Guid.NewGuid())[..2], readSet);
        readSet[0] = readSet[0] with { SourceSha256 = new('b', 64) };
        using (var holder = fixture.Resources.TryAcquire(Guid.NewGuid(),
            [new(ConfigurationKey(fixture.Extra), ResourceClaimMode.Mutation)]).Lease!)
            Assert.Throws<ConfigurationException>(() => fixture.Service.Commit(prepared, DateTimeOffset.UtcNow));
        var result = fixture.Service.Commit(prepared, DateTimeOffset.UtcNow);
        Assert.True(result.IsNew); Assert.Single(result.Items);
        Assert.Equal(2, fixture.Base.Revisions.Read(fixture.Main)!.Revision);
        Assert.Equal(1, fixture.Base.Revisions.Read(fixture.Extra)!.Revision);
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
        var operations = fixture.Base.Count("operations");
        Assert.Throws<ConfigurationException>(() => fixture.Service.Commit(prepared, DateTimeOffset.UtcNow));
        Assert.Equal(operations, fixture.Base.Count("operations"));
        Assert.Equal(1, fixture.Base.Revisions.Read(fixture.Main)!.Revision);
    }

    [Fact(DisplayName = "A Dependency Change At Sql Operation Acceptance Rolls Back Acceptance And Every Write")]
    public void ChecksAtomicReads()
    {
        using var fixture = new PatchBatchFixture();
        var current = fixture.Base.Revisions.Read(fixture.Extra)!;
        fixture.Base.Save(new([new(fixture.Extra, 1, 1, current.Contract, current.Tree, ConfigurationUpdateKind.Manual)]));
        using var prepared = fixture.Service.Preview("fixture", fixture.Request(Guid.NewGuid())[..2], [Stamp(fixture)]);
        fixture.Base.Execute("CREATE TRIGGER fixture_read_race AFTER INSERT ON operations WHEN NEW.operation_kind='configuration.patch-batch' " +
            "BEGIN UPDATE configuration_current SET revision=1 WHERE resource_id='extra'; END;");
        var operations = fixture.Base.Count("operations");
        Assert.Throws<ConfigurationException>(() => fixture.Service.Commit(prepared, DateTimeOffset.UtcNow));
        Assert.Equal(operations, fixture.Base.Count("operations"));
        Assert.Equal(1, fixture.Base.Revisions.Read(fixture.Main)!.Revision);
        Assert.Equal(2, fixture.Base.Revisions.Read(fixture.Extra)!.Revision);
        fixture.Base.Execute("DROP TRIGGER fixture_read_race;");
        Assert.True(fixture.Service.Commit(prepared, DateTimeOffset.UtcNow).IsNew);
    }

    [Fact(DisplayName = "Exact Completed Replay Binds The Read Set Without Reopening Its Now Changed Dependency")]
    public void BindsReadReplay()
    {
        using var fixture = new PatchBatchFixture();
        var request = fixture.Request(Guid.NewGuid())[..2];
        var stamp = Stamp(fixture);
        fixture.Service.Apply("fixture", request, DateTimeOffset.UtcNow, readSet: [stamp]);
        var current = fixture.Base.Revisions.Read(fixture.Extra)!;
        fixture.Base.Save(new([new(fixture.Extra, 1, 1, current.Contract, current.Tree, ConfigurationUpdateKind.Manual)]));
        var replay = fixture.Service.Apply("fixture", request, DateTimeOffset.UtcNow, readSet: [stamp]);
        Assert.False(replay.IsNew); Assert.Single(replay.Items); Assert.Equal(2, replay.Items[0].Saved.Revision);
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
        File.WriteAllText(Path.Combine(fixture.Content, stamp.SourceSha256 + ".bin"), "corrupted-read-only-input");
        var operations = fixture.Base.Count("operations");
        Assert.Throws<IOException>(() => fixture.Service.Commit(prepared, DateTimeOffset.UtcNow));
        Assert.Equal(operations, fixture.Base.Count("operations"));
        Assert.Equal(1, fixture.Base.Revisions.Read(fixture.Main)!.Revision);
    }

    [Fact(DisplayName = "The Read Dependency File And Parent Remain Physically Held Through Actual Sql Publication")]
    public void HoldsReadBytes()
    {
        using var fixture = new PatchBatchFixture();
        var stamp = Stamp(fixture); var calls = 0;
        var observer = new ReadObserver(() =>
        {
            Assert.Throws<IOException>(() => File.Delete(Path.Combine(fixture.Content, stamp.SourceSha256 + ".bin")));
            Assert.Throws<IOException>(() => Directory.Move(fixture.Content, fixture.Content + "-moved"));
            calls++;
        });
        var service = new PatchBatchService(fixture.Base.Instance, fixture.Base.Database, new(), fixture.Lease, fixture.Resources, observer);
        var result = service.Apply("fixture", fixture.Request(Guid.NewGuid())[..2], DateTimeOffset.UtcNow, readSet: [stamp]);
        Assert.True(result.IsNew); Assert.Equal(2, calls);
    }

    private static ConfigurationReadStamp Stamp(PatchBatchFixture fixture)
    {
        var current = fixture.Base.Revisions.Read(fixture.Extra)!;
        var entry = current.Tree.Entries.Single(item => item.RelativePath == "three.json");
        return new(fixture.Extra, current.Revision, 1, current.Contract, entry.RelativePath, entry.Sha256!);
    }

    private static string ConfigurationKey(ConfigurationAddress address) =>
        NexusPipeline.Platform.Storage.Configuration.ConfigurationRevisionService.ResourceKey(address);

    private sealed class ReadObserver(Action check) : IBatchPersistenceObserver
    {
        public void Reached(BatchPersistencePoint point, Guid operationId, int resourceOrdinal)
        {
            if (point is BatchPersistencePoint.OperationAccepted or BatchPersistencePoint.TransactionCommitted) check();
        }
    }
}
