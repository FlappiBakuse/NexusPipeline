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
        using var scope = JournalReadScope.Hold(store, id);
        Assert.Equal(1, reader.Read(id).Revision);
        using (JournalReadScope.Hold(reader, id)) Assert.Single(reader.ReadAllRevisions(id));
        if (fault == "unknown-entry")
        {
            var unknown = Path.Combine(directory, "unknown.bin");
            File.WriteAllText(unknown, "preserve");
            Assert.Throws<IOException>(() => reader.Read(id));
            Assert.Equal("preserve", File.ReadAllText(unknown));
            File.Delete(unknown);
            Assert.Single(reader.ReadAllRevisions(id));
            return;
        }
        var path = fault == "plan" ? plan : Path.Combine(directory, "0000000001.json");
        var bytes = File.ReadAllBytes(path);
        Assert.Throws<IOException>(() => File.WriteAllText(path, "changed"));
        Assert.Throws<IOException>(() => File.Move(path, path + ".replaced"));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        scope.Dispose();
        File.WriteAllText(path, "changed");
        Assert.ThrowsAny<Exception>(() => reader.Read(id));
        Assert.Equal("changed", File.ReadAllText(path));
    }

    [Fact(DisplayName = "Journal Read Scopes Keep Separate Instances And Restore Their Outer Owner")]
    public void RestoresOuterOwner()
    {
        var (first, firstId, _) = Fixture(); var (second, secondId, _) = Fixture();
        using var outer = JournalReadScope.Hold(first, firstId);
        using var inner = JournalReadScope.Hold(second, secondId);
        Assert.Equal(firstId, new JournalStore(first.Instance, first.Contracts).Read(firstId).JournalId);
        Assert.Equal(secondId, new JournalStore(second.Instance, second.Contracts).Read(secondId).JournalId);
        Assert.Throws<InvalidOperationException>(() => outer.Dispose());
        inner.Dispose();
        var path = Path.Combine(first.Instance.RuntimeRoot, "journals", firstId.ToString("D"), "0000000001.json");
        Assert.Throws<IOException>(() => File.WriteAllText(path, "changed"));
        Assert.Equal(firstId, new JournalStore(first.Instance, first.Contracts).Read(firstId).JournalId);
        outer.Dispose();
        File.WriteAllText(path, "changed");
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
        var next = System.Text.Json.Nodes.JsonNode.Parse(store.Read(first).Export())!.AsObject();
        var second = Guid.NewGuid(); next["journalId"] = second.ToString("D"); next["operationId"] = Guid.NewGuid().ToString("D");
        store.Create(JsonSerializer.SerializeToUtf8Bytes(next));
        using var parents = JournalReadScope.HoldParents(store.Instance, store.Contracts);
        JournalReadScope.SelectParent(store.Instance, store.Contracts, first);
        var firstPath = Path.Combine(store.Instance.RuntimeRoot, "journals", first.ToString("D"), "0000000001.json");
        using (JournalReadScope.HoldParents(store.Instance, store.Contracts))
        {
            JournalReadScope.SelectParent(store.Instance, store.Contracts, first);
            Assert.Equal(first, new JournalStore(store.Instance, store.Contracts).Read(first).JournalId);
        }
        Assert.Throws<IOException>(() => File.WriteAllText(firstPath, "changed"));
        using (JournalReadScope.Hold(new JournalStore(store.Instance, store.Contracts), first))
            Assert.Equal(first, new JournalStore(store.Instance, store.Contracts).Read(first).JournalId);
        JournalReadScope.SelectParent(store.Instance, store.Contracts, second);
        Assert.Equal(second, new JournalStore(store.Instance, store.Contracts).Read(second).JournalId);
        File.WriteAllText(firstPath, "changed");
        Assert.ThrowsAny<Exception>(() => JournalReadScope.SelectParent(store.Instance, store.Contracts, first));
        Assert.Equal("changed", File.ReadAllText(firstPath));
        JournalReadScope.SelectParent(store.Instance, store.Contracts, second);
        Assert.Equal(second, new JournalStore(store.Instance, store.Contracts).Read(second).JournalId);
    }
}
