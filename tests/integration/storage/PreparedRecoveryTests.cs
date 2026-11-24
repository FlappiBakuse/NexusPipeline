using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class PreparedRecoveryTests
{
    [Fact(DisplayName = "Prepared Attempt Recovery Preserves Configuration And Logs And Commits An Independent Blocked Attempt Receipt")]
    public void CommitsBlockedAttempt()
    {
        var fixture = AttemptTestFixture.Create();
        fixture.Accept();
        var log = File.ReadAllBytes(fixture.Plan.InitialLogs[0].Path);
        Assert.Equal(JournalRecoveryResult.NotExecuted, fixture.Recover());
        var snapshot = new RecoveryJournalRegistry(fixture.Database).ReadSnapshot();
        var registered = snapshot.Journals.Single();
        Assert.Equal(JournalPhase.Committed, registered.Phase);
        Assert.Empty(snapshot.Isolations);
        Assert.Empty(snapshot.FileEffects);
        Assert.Equal(EffectGateState.Sealed, new JournalEffectGate(fixture.Database).Read(fixture.JournalId).State);
        var facts = new AttemptReceiptStore(fixture.Database).Read(Guid.Parse(fixture.Plan.AttemptId))!.Facts;
        Assert.Equal(new AttemptFacts(Guid.Parse(fixture.Plan.RunId), Guid.Parse(fixture.Plan.AttemptId), 1, EngineOutcome.NotStarted,
            BusinessOutcome.Unverified, LifecycleOutcome.Blocked, RecoveryOutcome.NotRequired, "attempt_not_started_before_interruption"), facts);
        var scanner = new FileTreeScanner(SnapshotBudget.Default);
        var configuration = scanner.Capture(fixture.Configuration.TargetPath, default);
        Assert.Equal(fixture.Configuration.Before.Snapshot.Entries, configuration.Entries);
        Assert.Equal(fixture.Configuration.Before.OriginalObjects, PhysicalTreeIdentity.Capture(fixture.Configuration.TargetPath, configuration));
        Assert.Equal(log, File.ReadAllBytes(fixture.Plan.InitialLogs[0].Path));
        var before = JournalBytes(fixture);
        Assert.Equal(JournalRecoveryResult.AlreadyCompleted, fixture.Recover());
        Assert.Equal(snapshot.Revision, new RecoveryJournalRegistry(fixture.Database).ReadSnapshot().Revision);
        Assert.Equal(before, JournalBytes(fixture));
    }

    [Theory(DisplayName = "Unproven Attempt Sites Never Become No Effect Receipts Or Release Isolation")]
    [InlineData("running")]
    [InlineData("incomplete")]
    [InlineData("foreign-writer")]
    [InlineData("entered")]
    [InlineData("output")]
    [InlineData("log-directory")]
    [InlineData("configuration")]
    [InlineData("log-content")]
    [InlineData("image")]
    [InlineData("workspace")]
    [InlineData("live-log-writer")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void KeepsUnprovenIsolation(string fault)
    {
        var fixture = AttemptTestFixture.Create();
        fixture.Accept();
        var registry = new RecoveryJournalRegistry(fixture.Database);
        var logPath = fixture.Plan.InitialLogs[0].Path;
        var output = Path.Combine(fixture.Instance.RuntimeRoot, fixture.Plan.OutputDirectory);
        var logDirectory = Path.Combine(fixture.Instance.RuntimeRoot, fixture.Plan.LogDirectory);
        var configuration = Path.Combine(fixture.Configuration.TargetPath, "settings.bin");
        var originalImage = Path.Combine(fixture.Root, "original-image");
        var workspace = Path.Combine(Path.GetDirectoryName(fixture.Configuration.TargetPath)!, ".nexus-" + fixture.Configuration.JournalId.ToString("D"));
        var unknownOutput = Path.Combine(output, "unknown.bin");
        var unknownLog = Path.Combine(logDirectory, "unknown.bin");
        var unknownWorkspace = Path.Combine(workspace, "unknown.bin");
        if (fault == "entered") new JournalEffectGate(fixture.Database).Enter(registry.ReadSnapshot().Journals.Single(), 1);
        if (fault == "output") { Directory.CreateDirectory(output); File.WriteAllBytes(unknownOutput, [17, 18]); }
        if (fault == "log-directory") { Directory.CreateDirectory(logDirectory); File.WriteAllBytes(unknownLog, [29, 30]); }
        if (fault == "configuration") File.WriteAllBytes(configuration, [77]);
        if (fault == "log-content") File.WriteAllBytes(logPath, [88]);
        if (fault == "image")
        {
            File.Move(fixture.Source.Image, originalImage);
            File.WriteAllBytes(fixture.Source.Image, [0, 1, 255]);
        }
        if (fault == "workspace") { Directory.CreateDirectory(workspace); File.WriteAllBytes(unknownWorkspace, [43, 44]); }
        var directories = new[] { output, logDirectory, workspace, fixture.Configuration.TargetPath };
        var files = new[] { logPath, configuration, fixture.Source.Image, originalImage, unknownOutput, unknownLog, unknownWorkspace };
        var beforeDirectories = CaptureDirectories();
        var beforeFiles = RuntimeFingerprint.CaptureFileEvidence(files);
        using var writer = fault == "live-log-writer" ? new FileStream(logPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite) : null;
        Assert.Equal(JournalRecoveryResult.Quarantined, fixture.Recover(id => new(
            fault == "foreign-writer" ? Guid.NewGuid() : fixture.Instance.InstanceId, id, fault != "incomplete",
            fault == "running" ? WriterState.Running : WriterState.Stopped)));
        Assert.Equal(JournalPhase.Quarantined, registry.ReadSnapshot().Journals.Single().Phase);
        Assert.NotEmpty(registry.ReadSnapshot().Isolations);
        Assert.Null(new AttemptReceiptStore(fixture.Database).Read(Guid.Parse(fixture.Plan.AttemptId)));
        writer?.Dispose();
        Assert.Equal(beforeDirectories, CaptureDirectories());
        Assert.Equal(beforeFiles, RuntimeFingerprint.CaptureFileEvidence(files));
        var journal = JournalBytes(fixture);
        Assert.Equal(JournalRecoveryResult.Quarantined, fixture.Recover());
        Assert.Equal(journal, JournalBytes(fixture));
        Assert.Equal(beforeDirectories, CaptureDirectories());
        Assert.Equal(beforeFiles, RuntimeFingerprint.CaptureFileEvidence(files));

        (string Path, PhysicalObjectIdentity? Identity, string? Members)[] CaptureDirectories() =>
            directories.Select(path => Directory.Exists(path)
                ? (path, (PhysicalObjectIdentity?)PhysicalObjectIdentity.Read(path),
                    JsonSerializer.Serialize(Directory.EnumerateFileSystemEntries(path).Order(StringComparer.Ordinal)))
                : (path, null, null)).ToArray();
    }

    [Fact(DisplayName = "Unknown Attempt Intent Stops Read Only Preflight Before Any Journal Or Receipt Mutation")]
    public void PreservesUnknownIntents()
    {
        var fixture = AttemptTestFixture.Create();
        var accepted = fixture.Accept();
        var path = Path.Combine(fixture.Instance.RuntimeRoot, accepted.Plan.RelativePath);
        var bytes = File.ReadAllBytes(path);
        bytes[^1] = 0;
        File.WriteAllBytes(path, bytes);
        var before = JournalBytes(fixture);
        var snapshot = new RecoveryJournalRegistry(fixture.Database).ReadSnapshot();
        Assert.Throws<IOException>(() => fixture.Recover());
        Assert.Equal(before, JournalBytes(fixture));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(snapshot.Revision, new RecoveryJournalRegistry(fixture.Database).ReadSnapshot().Revision);
        Assert.Null(new AttemptReceiptStore(fixture.Database).Read(Guid.Parse(fixture.Plan.AttemptId)));
    }

    private static string[] JournalBytes(AttemptTestFixture fixture) => Directory.GetFiles(Path.Combine(fixture.Instance.RuntimeRoot, "journals"), "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(fixture.Instance.RuntimeRoot, path) + ":" +
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
}
