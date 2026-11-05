using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using System.Runtime.Versioning;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class PayloadRecoveryTests
{
    [Theory(DisplayName = "Prepared Payload Recovery Seals Entry And Commits No Effects Without Changing The Original Site")]
    [InlineData(JournalKind.UpdateApply, PayloadAction.Upgrade)]
    [InlineData(JournalKind.ExtensionApply, PayloadAction.Install)]
    [InlineData(JournalKind.ExtensionApply, PayloadAction.Upgrade)]
    [InlineData(JournalKind.ExtensionApply, PayloadAction.Remove)]
    public void SealsUnexecutedPayload(JournalKind kind, PayloadAction action)
    {
        using var fixture = PayloadTestFixture.Create(kind, action);
        fixture.Accept();
        var before = fixture.Scanner.Capture(fixture.Plan.TargetPath, default);
        Assert.Equal(JournalRecoveryResult.NotExecuted, fixture.Recover());
        var snapshot = new RecoveryJournalRegistry(fixture.Database).ReadSnapshot();
        var journal = Assert.Single(snapshot.Journals, item => item.JournalId == fixture.Plan.JournalId);
        Assert.Equal(JournalPhase.Committed, journal.Phase);
        Assert.Equal(3, journal.Revision);
        Assert.Empty(snapshot.Isolations);
        Assert.All(snapshot.FileEffects.Where(item => item.JournalId == fixture.Plan.JournalId), item => Assert.Equal(FileEffectState.NotExecuted, item.State));
        Assert.Equal(EffectGateState.Sealed, new JournalEffectGate(fixture.Database).Read(journal.JournalId).State);
        Assert.Equal(before.Entries, fixture.Scanner.Capture(fixture.Plan.TargetPath, default).Entries);
        var evidence = JournalBytes(fixture);
        Assert.Equal(JournalRecoveryResult.AlreadyCompleted, fixture.Recover());
        Assert.Equal(evidence, JournalBytes(fixture));
        Assert.Equal(snapshot.Revision, new RecoveryJournalRegistry(fixture.Database).ReadSnapshot().Revision);
    }

    [Theory(DisplayName = "Unproven Payload Sites Or Writers Remain Isolated And Never Become A No Effect Commit")]
    [InlineData("changed-bytes")]
    [InlineData("same-bytes-replaced")]
    [InlineData("unknown-workspace")]
    [InlineData("writer-running")]
    [InlineData("writer-incomplete")]
    [InlineData("writer-wrong-instance")]
    [InlineData("writer-wrong-journal")]
    [InlineData("gate-entered")]
    [InlineData("open-write-handle")]
    public void IsolatesUnprovenSites(string fault)
    {
        using var fixture = PayloadTestFixture.Create(JournalKind.ExtensionApply, PayloadAction.Upgrade);
        fixture.Accept();
        var targetFile = Path.Combine(fixture.Plan.TargetPath, fixture.Plan.Before.Snapshot.Entries
            .First(item => item.Kind == FileObjectKind.File).RelativePath);
        if (fault == "changed-bytes") File.WriteAllBytes(targetFile, [18, 33, 44]);
        if (fault == "same-bytes-replaced")
        {
            var original = File.ReadAllBytes(targetFile);
            File.Move(targetFile, Path.Combine(fixture.Root, "original-retained.bin"));
            File.WriteAllBytes(targetFile, original);
        }
        if (fault == "unknown-workspace") Directory.CreateDirectory(ExtensionWorkspacePath.For(fixture.Plan));
        if (fault == "gate-entered") new JournalEffectGate(fixture.Database).Enter(new RecoveryJournalRegistry(fixture.Database).ReadJournal(fixture.Plan.JournalId), 1);
        var before = File.ReadAllBytes(targetFile);
        using var writer = fault == "open-write-handle" ? new FileStream(targetFile, FileMode.Open, FileAccess.Write, FileShare.ReadWrite) : null;
        PayloadWriterObservation Observe(Guid id) => new(fault == "writer-wrong-instance" ? Guid.NewGuid() : fixture.Instance.InstanceId,
            fault == "writer-wrong-journal" ? Guid.NewGuid() : id, fault != "writer-incomplete", fault == "writer-running" ? WriterState.Running : WriterState.Stopped);
        if (fault == "gate-entered")
        {
            Assert.Throws<IOException>(() => fixture.Recover(Observe));
            Assert.Equal(before, File.ReadAllBytes(targetFile));
            Assert.Equal(EffectGateState.Entered, new JournalEffectGate(fixture.Database).Read(fixture.Plan.JournalId).State);
            Assert.Equal(JournalPhase.Prepared, new RecoveryJournalRegistry(fixture.Database).ReadJournal(fixture.Plan.JournalId).Phase);
            return;
        }
        Assert.Equal(JournalRecoveryResult.Quarantined, fixture.Recover(Observe));
        writer?.Dispose();
        Assert.Equal(before, File.ReadAllBytes(targetFile));
        var snapshot = new RecoveryJournalRegistry(fixture.Database).ReadSnapshot();
        Assert.Equal(JournalPhase.Quarantined, Assert.Single(snapshot.Journals, item => item.JournalId == fixture.Plan.JournalId).Phase);
        Assert.Equal(fixture.Plan.ResourceKeys.Count, snapshot.Isolations.Count(item => item.JournalId == fixture.Plan.JournalId));
        Assert.All(snapshot.FileEffects.Where(item => item.JournalId == fixture.Plan.JournalId), item => Assert.Equal(FileEffectState.Pending, item.State));
        var evidence = JournalBytes(fixture);
        Assert.Equal(JournalRecoveryResult.Quarantined, fixture.Recover());
        Assert.Equal(evidence, JournalBytes(fixture));
    }

    [Fact(DisplayName = "Unknown Payload Intent Bytes Are Rejected Before Any Recovery Metadata Is Changed")]
    public void PreservesUnknownIntents()
    {
        using var fixture = PayloadTestFixture.Create(JournalKind.UpdateApply, PayloadAction.Upgrade);
        var accepted = fixture.Accept();
        var plan = Path.Combine(fixture.Instance.RuntimeRoot, accepted.Plan.RelativePath);
        File.WriteAllText(plan, "{\"format\":\"unknown-preserve-this-intent\"}");
        var evidence = JournalBytes(fixture);
        var revision = new RecoveryJournalRegistry(fixture.Database).ReadSnapshot().Revision;
        Assert.ThrowsAny<Exception>(() => fixture.Recover());
        Assert.Equal(evidence, JournalBytes(fixture));
        Assert.Equal(revision, new RecoveryJournalRegistry(fixture.Database).ReadSnapshot().Revision);
        Assert.Equal(EffectGateState.Unentered, new JournalEffectGate(fixture.Database).Read(fixture.Plan.JournalId).State);
        Assert.Equal("{\"format\":\"unknown-preserve-this-intent\"}", File.ReadAllText(plan));
    }

    private static string[] JournalBytes(PayloadTestFixture fixture) => Directory.GetFiles(Path.Combine(fixture.Instance.RuntimeRoot, "journals"), "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(fixture.Instance.RuntimeRoot, path) + ":" +
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
}
