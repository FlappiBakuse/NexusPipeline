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
        var frozen = JsonNode.Parse(draft.Plan.Export())!;
        var parent = draft.Plan.ReadWire().PayloadParentIdentity;
        var started = new JsonObject
        {
            ["format"] = "nexus.secret.preparation.receipt", ["version"] = 1,
            ["instanceId"] = fixture.Instance.InstanceId.ToString("D"),
            ["operationId"] = frozen["operationId"]!.DeepClone(), ["journalId"] = frozen["journalId"]!.DeepClone(),
            ["writerId"] = writer.ToString("D"), ["stage"] = "started",
            ["planDigest"] = Convert.ToHexStringLower(SHA256.HashData(draft.Plan.Export())),
            ["candidate"] = frozen["candidate"]!.DeepClone(), ["parentIdentity"] = frozen["payloadParentIdentity"]!.DeepClone()
        };
        Assert.True(JsonNode.DeepEquals(started, JsonNode.Parse(receipts.Encode(draft.Plan, draft.Reference, writer, "started", null))));
        var before = receipts.Decode(draft.Plan, draft.Reference, Encoding.UTF8.GetBytes(started.ToJsonString()));
        Assert.Equal("started", before.Stage); Assert.Equal(writer.ToString("D"), before.WriterId); Assert.Null(before.Identity);
        var identity = new NexusPipeline.Platform.Storage.PhysicalObjectIdentity(parent.VolumeSerial, new string('a', 32));
        var completed = started.DeepClone().AsObject();
        completed["stage"] = "completed";
        completed["identity"] = new JsonObject { ["volumeSerial"] = identity.VolumeSerial, ["fileId"] = identity.FileId };
        Assert.True(JsonNode.DeepEquals(completed, JsonNode.Parse(receipts.Encode(draft.Plan, draft.Reference, writer, "completed", identity))));
        var observed = receipts.Decode(draft.Plan, draft.Reference, Encoding.UTF8.GetBytes(completed.ToJsonString()));
        Assert.Equal("completed", observed.Stage); Assert.Equal(parent.VolumeSerial, observed.Identity!.VolumeSerial);
        Assert.Equal(identity.FileId, observed.Identity.FileId);
        var changed = completed.DeepClone();
        changed["candidate"]!["sha256"] = new string('b', 64);
        Assert.Equal("secret_preparation_receipt_unbound",
            Assert.Throws<IOException>(() => receipts.Decode(draft.Plan, draft.Reference, Encoding.UTF8.GetBytes(changed.ToJsonString()))).Message);
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
        var commits = fixture.Database.CommitSequence;
        var codec = new PreparationPlanCodec(fixture.Instance, new(), fixture.Database);
        using var draft = codec.Freeze(SecretStoreIdentity(fixture, 1), Guid.NewGuid(), fixture.Key, 1, value);
        var wire = codec.Read(draft.Reference).ReadWire();
        Assert.Equal(prior.SecretId!.Value.ToString("D"), wire.BeforeSecretId);
        Assert.Equal("planned", wire.Retirement.State);
        Assert.Equal(prior, await fixture.Store.GetStateAsync(fixture.Key, default));
        Assert.Equal(commits, fixture.Database.CommitSequence);
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
