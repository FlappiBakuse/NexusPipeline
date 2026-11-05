using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class PlanAnchorTests
{
    [Fact(DisplayName = "A Coherently Rebound History And Sql Cannot Replace The Original Opaque Plan Between Physical Stages")]
    public async Task RejectsReboundHistory()
    {
        using var fixture = new PayloadAcceptanceFixture(savedEnabled: false);
        _ = fixture.Prepare(); var accepted = fixture.Accept();
        var instance = fixture.Package.Catalog.Instance;
        var contracts = fixture.Package.Catalog.Contracts;
        var codec = new PayloadPlanCodec(instance, contracts, fixture.Package.Blobs, fixture.Roots);
        var original = codec.Read(accepted.Plan);
        var source = new PayloadSourceCodec(instance, contracts).ReadMetadata(original.ExtensionSource!);
        var injected = false;
        var observer = new Observer((point, journalId, revision, _) =>
        {
            if (point != PayloadPersistencePoint.SqlProjectionCommitted || revision != 2) return;
            Assert.Equal(accepted.JournalId, journalId);
            var replacement = fixture.Package.Blobs.PersistContent(new byte[] { 11, 73, 29 }, default);
            var entry = original.Candidate.Snapshot.Entries.Single(item => item.RelativePath == "entry.dll");
            var alternative = original with
            {
                Candidate = original.Candidate with
                {
                    Snapshot = original.Candidate.Snapshot with
                    {
                        Entries = original.Candidate.Snapshot.Entries.Select(item => item.RelativePath == entry.RelativePath
                            ? item with { SizeBytes = replacement.SizeBytes, Sha256 = replacement.Sha256 } : item).ToArray()
                    },
                    Contents = original.Candidate.Contents.Where(item => item.Sha256 != entry.Sha256).Append(replacement).ToArray()
                }
            };
            using var frozen = codec.Freeze(alternative);
            Assert.NotEqual(accepted.Plan, frozen.Reference);
            Assert.Equal(original.ExtensionSource, frozen.Plan.ExtensionSource);
            var journals = new JournalStore(instance, contracts);
            var history = journals.ReadAllRevisions(journalId);
            var physical = new ExtensionPhysicalCodec(instance, contracts);
            var first = Assert.Single(physical.ReadHistory(history, original, accepted.Plan.Sha256));
            var reboundArtifact = physical.Persist(first.Record with { PlanDigest = frozen.Reference.Sha256 }, frozen.Plan);
            byte[]? newest = null;
            foreach (var document in history)
            {
                var json = JsonNode.Parse(document.Export())!.AsObject();
                json["planPath"] = frozen.Reference.RelativePath;
                json["planDigest"] = frozen.Reference.Sha256;
                if (document.Revision == 2)
                    json["participants"]![0]!["identity"]!["artifact"] = new JsonObject
                    {
                        ["relativePath"] = reboundArtifact.RelativePath,
                        ["sizeBytes"] = reboundArtifact.SizeBytes,
                        ["sha256"] = reboundArtifact.Sha256
                    };
                newest = BoundedJson.Serialize(json);
                File.WriteAllBytes(Path.Combine(instance.RuntimeRoot, "journals", journalId.ToString("D"),
                    document.Revision.ToString("D10", global::System.Globalization.CultureInfo.InvariantCulture) + ".json"), newest);
            }
            var receipt = Convert.ToHexStringLower(SHA256.HashData(newest!));
            fixture.Package.Catalog.Database.Write((connection, transaction) =>
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "UPDATE operation_journals SET plan_path=$path,plan_digest=$digest WHERE journal_id=$journal;";
                command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
                command.Parameters.AddWithValue("$path", frozen.Reference.RelativePath);
                command.Parameters.AddWithValue("$digest", frozen.Reference.Sha256);
                Assert.Equal(1, command.ExecuteNonQuery());
                command.CommandText = "UPDATE file_effects SET intent_digest=$digest,receipt_digest=$receipt WHERE journal_id=$journal;";
                command.Parameters.AddWithValue("$receipt", receipt); Assert.Equal(1, command.ExecuteNonQuery());
                return true;
            });
            var rebound = new RecoveryJournalRegistry(fixture.Package.Catalog.Database).ReadJournal(journalId);
            var coherent = journals.ReadAllRevisions(journalId);
            Assert.Equal(frozen.Reference.Sha256, Assert.Single(
                physical.ReadHistory(coherent, frozen.Plan, frozen.Reference.Sha256)).Record.PlanDigest);
            Assert.All(coherent, document => Assert.Equal(frozen.Reference.Sha256, document.PlanDigest));
            fixture.Package.Catalog.Database.Write((connection, transaction) =>
            {
                PayloadEffectCursor.RequireInside(connection, transaction, rebound, source,
                    ExtensionApplyRequest.FromSource(source).Identity(fixture.Package.OperationId, "local"));
                JournalEffectGate.Require(connection, transaction, journalId, EffectGateState.Entered, true);
                return true;
            });
            var effect = Assert.Single(new FileEffectOutbox(fixture.Package.Catalog.Database).Read(journalId));
            Assert.Equal(FileEffectState.Started, effect.State);
            Assert.Equal(frozen.Reference.Sha256, effect.IntentDigest);
            Assert.Equal(receipt, effect.ReceiptDigest);
            injected = true;
        });
        var inventory = new ExtensionActivator(contracts, fixture.Package.Catalog.Lease, fixture.Package.Catalog.Resources);
        using var scope = await inventory.ReserveForApplyAsync(fixture.Package.OperationId, "fixture-plugin", default);
        var failure = Assert.Throws<IOException>(() => new ExtensionPhysicalJournal(instance,
            fixture.Package.Catalog.Database, contracts, fixture.Roots, observer).Apply(fixture.Prepared!, scope, default));
        Assert.True(injected);
        Assert.Equal("extension_payload_plan_reference_changed", failure.Message);
        Assert.False(Directory.Exists(fixture.Target));
        Assert.False(Directory.Exists(ExtensionWorkspacePath.For(original)));
        Assert.Equal(2, new JournalStore(instance, contracts).ReadAllRevisions(accepted.JournalId).Count);
    }

    private sealed class Observer(Action<PayloadPersistencePoint, Guid, long, JournalPhase> action) : IPayloadPersistenceObserver
    {
        public void Reached(PayloadPersistencePoint point, Guid journalId, long revision, JournalPhase phase) =>
            action(point, journalId, revision, phase);
    }
}
