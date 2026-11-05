using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class PcHistoryTests
{
    [Fact(DisplayName = "Actual Retained Target History Protects Raw Output Without Reopening It And Damaged Target Metadata Blocks Cleanup")]
    public async Task ProtectsTargetHistory()
    {
        var fixture = new PcStageFixture(forceClose: false, hooks: false);
        await fixture.ExecuteAsync(async () =>
        {
            var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
            Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
            var source = fixture.Source; var state = fixture.State;
            var blobs = new ImmutableBlobStore(source.Instance, new());
            var parent = state.Journal;
            var plan = new BlobReference(parent.PlanPath, new FileInfo(Path.Combine(source.Instance.RuntimeRoot, parent.PlanPath)).Length, parent.PlanDigest);
            var closure = new PlanReferenceClosure(source.Instance, new(), blobs, source.Database);
            var references = closure.Read(parent, plan);
            var result = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total));
            var main = StepEvidenceCodec.Read(blobs, result.Binding, result.Latest.Artifact);
            var part = RawOutputCodec.Read(blobs, result.Binding, main.RawOutput!).Parts.First().Content;
            var index = new BlobReferenceIndex(source.Instance, new(), source.Database, blobs);
            using (var exclusive = new FileStream(Path.Combine(source.Instance.RuntimeRoot, part.RelativePath), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.Equal(references, closure.Read(parent, plan));
                Assert.True(index.IsReferenced(part));
                Assert.Throws<IOException>(() => new StepEvidenceReader(source.Instance, source.Database, new())
                    .Read(result.Binding, result.Latest.Artifact, true));
            }
            var candidate = Assert.Single(new CleanupPlanCodec(source.Instance, new(), blobs).Capture(Guid.NewGuid(), Guid.NewGuid(), [part]).Items);
            Assert.Equal(CleanupStepResult.SkippedReferenced, new BlobCleanup(source.Instance, blobs, index).RemoveVerified(candidate));
            blobs.Verify(part);
            var stage = Assert.Single(PcStageCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256));
            var target = Assert.Single(TargetJobCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256));
            Assert.Equal("retained", target.Stage);
            var unrelated = blobs.PersistContent(Guid.NewGuid().ToByteArray(), default);
            Assert.False(index.IsReferenced(unrelated));
            var targetArtifacts = TargetJobCodec.ReadHistory(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)
                .Select(item => item.Evidence).OfType<BlobReference>().Append(stage.Artifact).Distinct();
            foreach (var artifact in targetArtifacts)
            {
                var path = Path.Combine(source.Instance.RuntimeRoot, artifact.RelativePath);
                var original = File.ReadAllBytes(path); var damaged = original.ToArray(); damaged[0] ^= 1;
                try
                {
                    File.WriteAllBytes(path, damaged);
                    Assert.Throws<IOException>(() => closure.Read(parent, plan));
                    Assert.Throws<IOException>(() => index.IsReferenced(unrelated));
                    Assert.Equal(damaged, File.ReadAllBytes(path));
                    var current = new RecoveryJournalRegistry(source.Database).ReadSnapshot().Journals.Single(item => item.JournalId == parent.JournalId);
                    Assert.True(System.Text.Json.JsonElement.DeepEquals(System.Text.Json.JsonSerializer.SerializeToElement(parent),
                        System.Text.Json.JsonSerializer.SerializeToElement(current)));
                }
                finally { File.WriteAllBytes(path, original); }
                Assert.Equal(references, closure.Read(parent, plan));
            }
        });
    }
}
