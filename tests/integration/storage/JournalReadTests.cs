using System.Text.Json;
using System.Runtime.Versioning;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class JournalReadTests
{
    [Theory(DisplayName = "Nested Journal Readers Share Protected Bytes And Still Reject Added Entries And Cold Tampering")]
    [InlineData("history")]
    [InlineData("plan")]
    [InlineData("unknown-entry")]
    public void ProtectsSharedReads(string fault)
    {
        var (store, id, plan) = Fixture();
        var directory = Path.Combine(store.Instance.RuntimeRoot, "journals", id.ToString("D"));
        var reader = new JournalStore(store.Instance, store.Contracts);
        var original = store.Read(id).Export();
        using var scope = JournalReadScope.Hold(store, id);
        Assert.Equal(original, reader.Read(id).Export());
        using (JournalReadScope.Hold(reader, id))
            Assert.Equal(new[] { original }, reader.ReadAllRevisions(id).Select(item => item.Export()));
        if (fault == "unknown-entry")
        {
            var unknown = Path.Combine(directory, "unknown.bin");
            File.WriteAllText(unknown, "preserve");
            Assert.Equal("journal_protected_history_changed", Assert.Throws<IOException>(() => reader.Read(id)).Message);
            Assert.Equal("preserve", File.ReadAllText(unknown));
            File.Delete(unknown);
            Assert.Equal(new[] { original }, reader.ReadAllRevisions(id).Select(item => item.Export()));
            return;
        }
        var path = fault == "plan" ? plan : HistoryPath(store, id);
        var bytes = File.ReadAllBytes(path);
        Assert.Throws<IOException>(() => File.WriteAllText(path, "changed"));
        Assert.Throws<IOException>(() => File.Move(path, path + ".replaced"));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(original, reader.Read(id).Export());
        scope.Dispose();
        File.WriteAllText(path, "changed");
        AssertTampering(fault, () => reader.Read(id));
        Assert.Equal("changed", File.ReadAllText(path));
        AssertTampering(fault, () => { using var rejected = JournalReadScope.Hold(reader, id); });
        File.WriteAllBytes(path, bytes);
        using var restored = JournalReadScope.Hold(reader, id);
        Assert.Equal(original, reader.Read(id).Export());
    }

    [Fact(DisplayName = "Journal Read Scopes Keep Separate Instances And Restore Their Outer Owner")]
    public void RestoresOuterOwner()
    {
        var (first, firstId, _) = Fixture(); var (second, secondId, _) = Fixture();
        var firstReader = new JournalStore(first.Instance, first.Contracts);
        var secondReader = new JournalStore(second.Instance, second.Contracts);
        var firstBytes = first.Read(firstId).Export(); var secondBytes = second.Read(secondId).Export();
        var firstPath = HistoryPath(first, firstId); var secondPath = HistoryPath(second, secondId);
        using var outer = JournalReadScope.Hold(first, firstId);
        using var inner = JournalReadScope.Hold(second, secondId);
        Assert.Equal(firstBytes, firstReader.Read(firstId).Export());
        Assert.Equal(secondBytes, secondReader.Read(secondId).Export());
        Assert.Equal("journal_read_scope_order", Assert.Throws<InvalidOperationException>(() => outer.Dispose()).Message);
        Assert.Throws<IOException>(() => File.WriteAllText(firstPath, "changed"));
        Assert.Throws<IOException>(() => File.WriteAllText(secondPath, "changed"));
        inner.Dispose();
        using (File.Open(secondPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        Assert.Throws<IOException>(() => File.WriteAllText(firstPath, "changed"));
        Assert.Equal(firstBytes, firstReader.Read(firstId).Export());
        outer.Dispose();
        File.WriteAllText(firstPath, "changed");
        AssertTampering("history", () => firstReader.Read(firstId));
        Assert.Equal(secondBytes, secondReader.Read(secondId).Export());
    }

    private static (JournalStore Store, Guid Id, string Plan) Fixture()
    {
        var contracts = new ContractReader();
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "journal-read-" + Guid.NewGuid().ToString("N"));
        var instance = new InstanceRoot(contracts).OpenOrCreate(root);
        var plan = new ImmutableBlobStore(instance, contracts).PersistPlan("{\"fixture\":true}"u8.ToArray());
        var id = Guid.NewGuid(); var store = new JournalStore(instance, contracts);
        store.Create(JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "nexus.execution.journal", version = 1, instanceId = instance.InstanceId, journalId = id,
            operationId = Guid.NewGuid(), operationKind = "configExchange", phase = "prepared", revision = 1,
            updatedAt = "2026-10-19T00:00:00Z", planPath = plan.RelativePath, planDigest = plan.Sha256,
            resourceKeys = new[] { "fixture:configuration" }, participants = Array.Empty<object>(), receipts = Array.Empty<object>()
        }));
        return (store, id, Path.Combine(root, plan.RelativePath));
    }

    [Fact(DisplayName = "A Parent Inventory Protects One Journal At A Time And Revalidates A Revisited Changed Parent")]
    public void SwitchesParents()
    {
        var (store, first, _) = Fixture();
        var original = store.Read(first).Export();
        var next = System.Text.Json.Nodes.JsonNode.Parse(original)!.AsObject();
        var second = Guid.NewGuid(); next["journalId"] = second.ToString("D"); next["operationId"] = Guid.NewGuid().ToString("D");
        var secondBytes = store.Create(JsonSerializer.SerializeToUtf8Bytes(next)).Export();
        var reader = new JournalStore(store.Instance, store.Contracts);
        using var parents = JournalReadScope.HoldParents(store.Instance, store.Contracts);
        JournalReadScope.SelectParent(store.Instance, store.Contracts, first);
        var firstPath = HistoryPath(store, first); var secondPath = HistoryPath(store, second);
        using (File.Open(secondPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        using (JournalReadScope.HoldParents(store.Instance, store.Contracts))
        {
            JournalReadScope.SelectParent(store.Instance, store.Contracts, first);
            Assert.Equal(original, reader.Read(first).Export());
            JournalReadScope.SelectParent(store.Instance, store.Contracts, second);
            using (var held = new FileStream(secondPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                held.Lock(0, held.Length);
                try { Assert.Equal(secondBytes, reader.Read(second).Export()); }
                finally { held.Unlock(0, held.Length); }
            }
            JournalReadScope.SelectParent(store.Instance, store.Contracts, first);
            Assert.Equal(original, reader.Read(first).Export());
        }
        Assert.Throws<IOException>(() => File.WriteAllText(firstPath, "changed"));
        using (JournalReadScope.Hold(reader, first)) Assert.Equal(original, reader.Read(first).Export());
        JournalReadScope.SelectParent(store.Instance, store.Contracts, second);
        Assert.Equal(secondBytes, reader.Read(second).Export());
        Assert.Throws<IOException>(() => File.WriteAllText(secondPath, "changed"));
        File.WriteAllText(firstPath, "changed");
        AssertTampering("history", () => JournalReadScope.SelectParent(store.Instance, store.Contracts, first));
        Assert.Equal("changed", File.ReadAllText(firstPath));
        JournalReadScope.SelectParent(store.Instance, store.Contracts, second);
        Assert.Equal(secondBytes, reader.Read(second).Export());
    }

    private static string HistoryPath(JournalStore store, Guid journal) =>
        Path.Combine(store.Instance.RuntimeRoot, "journals", journal.ToString("D"), "0000000001.json");

    [Theory(DisplayName = "Publication Appends Stay Visible And Remain Correct Beyond The Protected History Budget")]
    [InlineData(2)]
    [InlineData(260)]
    public void PublishesProtectedAppend(int revisions)
    {
        var (store, id, _) = Fixture();
        using var lease = InstanceLease.Acquire(store.Instance, new());
        lease.PublishExecution(() =>
        {
            var reader = new JournalStore(store.Instance, new());
            JournalReadScope.SelectParent(store.Instance, new(), id);
            for (var revision = 2; revision <= revisions; revision++)
            {
                var next = System.Text.Json.Nodes.JsonNode.Parse(reader.Read(id).Export())!.AsObject();
                next["revision"] = revision;
                next["updatedAt"] = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc).AddSeconds(revision).ToString("O");
                next["participants"]!.AsArray().Add(JsonSerializer.SerializeToNode(new
                {
                    participantId = Guid.NewGuid(), kind = "database", resourceKey = "fixture:configuration",
                    identity = new { revision }
                }));
                _ = reader.Append(id, revision - 1, JsonSerializer.SerializeToUtf8Bytes(next));
                Assert.Equal(revision, store.Read(id).Revision);
            }
            Assert.Equal(Enumerable.Range(1, revisions).Select(value => (long)value), reader.ReadAllRevisions(id).Select(item => item.Revision));
            if (revisions <= 256) Assert.Throws<IOException>(() => File.WriteAllText(HistoryPath(store, id), "changed"));
        });
        var original = File.ReadAllBytes(HistoryPath(store, id));
        File.WriteAllBytes(HistoryPath(store, id), original);
        Assert.Equal(revisions, store.Read(id).Revision);
    }

    private static void AssertTampering(string fault, Action read)
    {
        if (fault == "plan") Assert.Equal("journal_plan_digest_mismatch", Assert.Throws<IOException>(read).Message);
        else Assert.Equal("invalid_json", Assert.Throws<JsonException>(read).Message);
    }
}
