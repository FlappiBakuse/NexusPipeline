using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class CrossVolumeTests
{
    [Theory(DisplayName = "Actual Editor Across Two Native Volumes Restores Original Objects Before Saving Or Cancelling The Whole Group")]
    [InlineData("missing", false)]
    [InlineData("missing", true)]
    [InlineData("file", false)]
    [InlineData("file", true)]
    [InlineData("directory", false)]
    [InlineData("directory", true)]
    public async Task RestoresCrossVolume(string shape, bool save)
    {
        var volumeRoot = Environment.GetEnvironmentVariable("NEXUS_TEST_VOLUME_ROOT") ?? throw new IOException("fixture_second_volume_required");
        var targetRoot = Path.Combine(volumeRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(targetRoot);
        var directory = shape == "directory";
        await using var fixture = new EditControllerFixture(originalShape: shape, outputShape: directory ? "directory" : "file",
            directorySnapshot: directory, targetRoot: targetRoot);
        var runtimeVolume = PhysicalObjectIdentity.Read(fixture.Base.Instance.RuntimeRoot).VolumeSerial;
        var targetVolume = PhysicalObjectIdentity.Read(targetRoot).VolumeSerial;
        Assert.NotEqual(runtimeVolume, targetVolume);
        var root = await fixture.BeginAsync();
        var rootIdentity = ProcessIdentityReader.Read(root);
        var status = fixture.Controller.Read(fixture.SessionId, "fixture")!;
        Assert.Equal(ConfigurationEditPhase.Editing, status.Phase);
        foreach (var resource in status.Resources)
        {
            var physical = new JournalStore(fixture.Base.Instance, new()).Read(resource.JournalId);
            var prepared = ConfigurationJournalBinding.Read(fixture.Base.Instance, physical, new(), fixture.Base.Database).Prepared!;
            Assert.Equal(targetVolume, prepared.Plan.ParentIdentity.VolumeSerial);
            Assert.Equal(targetVolume, PhysicalObjectIdentity.Read(prepared.WorkPath).VolumeSerial);
            Assert.Equal(targetVolume, PhysicalObjectIdentity.Read(prepared.Plan.TargetPath).VolumeSerial);
            foreach (var blob in prepared.Plan.Candidate.Contents.Concat(prepared.Plan.Before.Contents))
                Assert.Equal(runtimeVolume, PhysicalObjectIdentity.Read(Path.Combine(fixture.Base.Instance.RuntimeRoot, blob.RelativePath)).VolumeSerial);
        }
        if (save) await fixture.Controller.SaveAsync(Guid.NewGuid(), "fixture", fixture.SessionId, status.Revision, DateTimeOffset.UtcNow);
        else await fixture.Controller.CancelAsync(Guid.NewGuid(), "fixture", fixture.SessionId, status.Revision, DateTimeOffset.UtcNow);
        fixture.VerifyOriginals();
        foreach (var resource in fixture.Request.Targets)
        {
            var saved = fixture.Base.Revisions.Read(resource.Address)!;
            Assert.Equal(save ? 2 : 1, saved.Revision);
            if (save)
            {
                Assert.Equal(directory ? FileObjectKind.Directory : FileObjectKind.File, saved.Tree.Shape);
                var entry = Assert.Single(saved.Tree.Entries, item => item.Kind == FileObjectKind.File);
                var bytes = File.ReadAllText(Path.Combine(fixture.Base.Instance.RuntimeRoot, "blobs", "content", entry.Sha256 + ".bin"));
                Assert.Equal(directory ? "edited-directory" : "{\"edited\":true}", bytes);
            }
        }
        var finished = fixture.Controller.Read(fixture.SessionId, "fixture")!;
        Assert.Equal(save ? ConfigurationEditPhase.Saved : ConfigurationEditPhase.Cancelled, finished.Phase);
        Assert.Empty(fixture.Index.Current.ResourceHolds);
        Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(rootIdentity));
        File.WriteAllBytes(Path.Combine(fixture.Base.Root, "cross-volume-evidence.json"), JsonSerializer.SerializeToUtf8Bytes(new
            { runtimeRoot = fixture.Base.Instance.RuntimeRoot, targetRoot, runtimeVolume, targetVolume, shape, save, rootProcessId = root }));
    }

}
