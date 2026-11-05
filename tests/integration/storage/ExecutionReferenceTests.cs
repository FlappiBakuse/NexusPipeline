using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class ExecutionReferenceTests
{
    [Fact(DisplayName = "Prepared And Committed Attempts Retain Definition Log And Both Configuration Trees Under The Actual Deletion Guard")]
    public void ProtectsAttemptClosure()
    {
        var fixture = AttemptTestFixture.Create();
        fixture.Accept();
        var definition = fixture.Source.Plan.DefinitionBundle;
        var contents = fixture.Configuration.Before.Contents.Concat(fixture.Configuration.Candidate.Contents).Append(
            new(definition.RelativePath, definition.SizeBytes, definition.Sha256)).Concat(fixture.Plan.InitialLogs.SelectMany(log => log.Before.Entries)
                .Where(entry => entry.Kind == "file").Select(entry => new BlobReference($"blobs/content/{entry.Sha256}.bin", entry.SizeBytes, entry.Sha256!))).Distinct().ToArray();
        var index = new BlobReferenceIndex(fixture.Instance, new(), fixture.Database, fixture.Source.Blobs);
        var cleanup = new BlobCleanup(fixture.Instance, fixture.Source.Blobs, index);
        var candidates = new CleanupPlanCodec(fixture.Instance, new(), fixture.Source.Blobs).Capture(Guid.NewGuid(), Guid.NewGuid(), contents);
        foreach (var item in candidates.Items) Assert.Equal(CleanupStepResult.SkippedReferenced, cleanup.RemoveVerified(item));
        fixture.Recover();
        foreach (var item in candidates.Items)
        {
            Assert.Equal(CleanupStepResult.SkippedReferenced, cleanup.RemoveVerified(item));
            fixture.Source.Blobs.Verify(item.Blob);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    [Theory(DisplayName = "Payload Reference Discovery Requires Owned Roots And Retains Both Trees Before And After No Effect Commit")]
    [InlineData(JournalKind.UpdateApply)]
    [InlineData(JournalKind.ExtensionApply)]
    public void ProtectsOwnedPayloads(JournalKind kind)
    {
        using var fixture = PayloadTestFixture.Create(kind, PayloadAction.Upgrade);
        fixture.Accept();
        var blobs = new ImmutableBlobStore(fixture.Instance, new());
        var candidates = new CleanupPlanCodec(fixture.Instance, new(), blobs).Capture(Guid.NewGuid(), Guid.NewGuid(), fixture.Plan.Before.Contents.Concat(fixture.Plan.Candidate.Contents).Distinct().ToArray());
        var unbound = new BlobCleanup(fixture.Instance, blobs, new(fixture.Instance, new(), fixture.Database, blobs));
        foreach (var item in candidates.Items) Assert.Throws<IOException>(() => unbound.RemoveVerified(item));
        var cleanup = new BlobCleanup(fixture.Instance, blobs, new(fixture.Instance, new(), fixture.Database, blobs, fixture.Roots));
        foreach (var item in candidates.Items) Assert.Equal(CleanupStepResult.SkippedReferenced, cleanup.RemoveVerified(item));
        fixture.Recover();
        foreach (var item in candidates.Items)
        {
            Assert.Equal(CleanupStepResult.SkippedReferenced, cleanup.RemoveVerified(item));
            blobs.Verify(item.Blob);
        }
    }
}
