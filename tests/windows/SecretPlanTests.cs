using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class SecretPlanTests
{
    private const string Canary = "secret-preparation-plan-fixture-canary";

    [Fact(DisplayName = "Secret Receipt Contracts Bind The Frozen Plan And Require An Identity Only For Completed Stages")]
    public void BindsReceiptIdentity()
    {
        using var fixture = SecretTestRuntime.Create();
        using var value = new SecretValue(Canary);
        var codec = new PreparationPlanCodec(fixture.Instance, new(), fixture.Database);
        using var draft = codec.Freeze(SecretStoreIdentity(fixture, 0), Guid.NewGuid(), fixture.Key, 0, value);
        var receipts = new PreparationReceiptCodec(new());
        var writer = Guid.NewGuid();
        var started = receipts.Encode(draft.Plan, draft.Reference, writer, "started", null);
        Assert.Null(receipts.Decode(draft.Plan, draft.Reference, started).Identity);
        var parent = draft.Plan.ReadWire().PayloadParentIdentity;
        var identity = new NexusPipeline.Platform.Storage.PhysicalObjectIdentity(parent.VolumeSerial, new string('a', 32));
        var completed = receipts.Encode(draft.Plan, draft.Reference, writer, "completed", identity);
        Assert.Equal(identity.FileId, receipts.Decode(draft.Plan, draft.Reference, completed).Identity!.FileId);
        var changed = JsonNode.Parse(completed)!;
        changed["candidate"]!["sha256"] = new string('b', 64);
        Assert.Throws<IOException>(() => receipts.Decode(draft.Plan, draft.Reference, Encoding.UTF8.GetBytes(changed.ToJsonString())));
        Assert.Throws<System.Text.Json.JsonException>(() => receipts.Encode(draft.Plan, draft.Reference, writer, "completed", null));
    }

    [Fact(DisplayName = "Freezing A Secret Candidate Owns Its Plan Bytes Without Publishing The Field Or Writing Its Ciphertext")]
    public async Task OwnsFrozenPlans()
    {
        using var fixture = SecretTestRuntime.Create();
        using var value = new SecretValue(Canary);
        var operation = SecretStoreIdentity(fixture, 0);
        var codec = new PreparationPlanCodec(fixture.Instance, new(), fixture.Database);
        using var draft = codec.Freeze(operation, Guid.NewGuid(), fixture.Key, 0, value);
        var wire = draft.Plan.ReadWire();
        Assert.Equal("none", wire.Retirement.State);
        Assert.False(File.Exists(Path.Combine(fixture.Instance.RuntimeRoot, wire.Candidate.RelativePath)));
        Assert.False((await fixture.Store.GetStateAsync(fixture.Key, default)).IsConfigured);
        Assert.Empty(new RecoveryJournalRegistry(fixture.Database).ReadSnapshot().Journals);
        var original = draft.Plan.Export();
        Assert.True(original.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Canary)) < 0);
        Assert.True(original.AsSpan().IndexOf(Encoding.Unicode.GetBytes(Canary)) < 0);
        Assert.NotEqual(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canary))), wire.ReplayProof);
        var exported = draft.Plan.Export(); exported[0] ^= 1;
        Assert.Equal(original, draft.Plan.Export());
        var input = draft.Plan.Export(); var decoded = codec.Decode(input); input[0] ^= 1;
        Assert.Equal(original, decoded.Export());
        Assert.Equal(original, codec.Read(draft.Reference).Export());
    }

    [Fact(DisplayName = "A Replacement Plan Binds The Exact Prior Secret And Its Cleanup Without Retiring The Live Reference During Freeze")]
    public async Task FreezesReplacementCleanup()
    {
        using var fixture = SecretTestRuntime.Create();
        using var value = new SecretValue(Canary);
        var prior = await fixture.Store.ApplyAsync(Guid.NewGuid(), fixture.Key, 0, SecretDisposition.Set, value, default);
        var beforeJournals = System.Text.Json.JsonSerializer.Serialize(new RecoveryJournalRegistry(fixture.Database).ReadSnapshot());
        var codec = new PreparationPlanCodec(fixture.Instance, new(), fixture.Database);
        using var draft = codec.Freeze(SecretStoreIdentity(fixture, 1), Guid.NewGuid(), fixture.Key, 1, value);
        var wire = draft.Plan.ReadWire();
        Assert.Equal(prior.SecretId!.Value.ToString("D"), wire.BeforeSecretId);
        Assert.Equal("planned", wire.Retirement.State);
        Assert.NotNull(wire.Retirement.Plan);
        Assert.NotEqual(wire.JournalId, wire.Retirement.JournalId);
        Assert.Equal(prior, await fixture.Store.GetStateAsync(fixture.Key, default));
        Assert.Equal(beforeJournals, System.Text.Json.JsonSerializer.Serialize(new RecoveryJournalRegistry(fixture.Database).ReadSnapshot()));
        Assert.False(File.Exists(Path.Combine(fixture.Instance.RuntimeRoot, wire.Candidate.RelativePath)));
    }

    [Theory(DisplayName = "A Secret Plan Cannot Borrow Another Instance Or Mismatch Its Candidate And Prior Reference")]
    [InlineData("instance")]
    [InlineData("candidate-path")]
    [InlineData("retirement")]
    public void RejectsUnboundPlans(string fault)
    {
        using var fixture = SecretTestRuntime.Create();
        using var value = new SecretValue(Canary);
        var codec = new PreparationPlanCodec(fixture.Instance, new(), fixture.Database);
        using var draft = codec.Freeze(SecretStoreIdentity(fixture, 0), Guid.NewGuid(), fixture.Key, 0, value);
        var root = JsonNode.Parse(draft.Plan.Export())!;
        if (fault == "instance") root["instanceId"] = Guid.NewGuid().ToString("D");
        if (fault == "candidate-path") root["candidate"]!["secretId"] = Guid.NewGuid().ToString("D");
        if (fault == "retirement") root["beforeSecretId"] = Guid.NewGuid().ToString("D");
        Assert.Throws<IOException>(() => codec.Decode(Encoding.UTF8.GetBytes(root.ToJsonString())));
    }

    private static NexusPipeline.Core.Operations.OperationIdentity SecretStoreIdentity(SecretTestRuntime fixture, long revision) =>
        SecretFieldStore.Identity(Guid.NewGuid(), fixture.Key, revision, SecretDisposition.Set);
}
