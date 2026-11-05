using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class MixedRecoveryTests
{
    [Fact(DisplayName = "All Six Real Prepared Handlers Close Their Own Plans Before The Recovery Index Becomes Ready")]
    public void RecoversTypedKinds()
    {
        var fixture = MixedRecoveryFixture.Create(false);
        using var lease = InstanceLease.Acquire(fixture.Attempt.Instance, new());
        var evidence = new JournalRecoveryEvidence(fixture.Attempt.Instance, new(fixture.Attempt.Instance, new()), fixture.Attempt.Database);
        var index = new RecoveryIndex();
        var coordinator = new RecoveryCoordinator(fixture.Registry, evidence, index);
        var results = coordinator.RecoverAndPublish(evidence, fixture.Handlers());
        Assert.Equal(6, results.Count);
        Assert.All(results.Values, result => Assert.Equal(JournalRecoveryResult.NotExecuted, result));
        Assert.True(index.Current.IsReady);
        Assert.Empty(index.Current.Isolations);
        Assert.All(fixture.Registry.ReadSnapshot().Journals, item => Assert.Equal(JournalPhase.Committed, item.Phase));
        Assert.NotNull(new AttemptReceiptStore(fixture.Attempt.Database).Read(Guid.Parse(fixture.Attempt.Plan.AttemptId)));
        var files = Fingerprints(fixture.Attempt.Instance.RuntimeRoot);
        var repeated = coordinator.RecoverAndPublish(evidence, fixture.Handlers());
        var retained = Assert.Single(repeated);
        Assert.Equal(JournalKind.UpdateApply, fixture.Registry.ReadJournal(retained.Key).Kind);
        Assert.Equal(JournalRecoveryResult.AlreadyCompleted, retained.Value);
        Assert.Equal(files.Where(item => !item.StartsWith("state/", StringComparison.Ordinal)),
            Fingerprints(fixture.Attempt.Instance.RuntimeRoot).Where(item => !item.StartsWith("state/", StringComparison.Ordinal)));
    }

    [Fact(DisplayName = "A Late Unknown Typed Plan With A Valid Digest Prevents Every Earlier Prepared Recovery Write")]
    public void RejectsUnknownKinds()
    {
        var fixture = MixedRecoveryFixture.Create(true);
        using var lease = InstanceLease.Acquire(fixture.Attempt.Instance, new());
        var evidence = new JournalRecoveryEvidence(fixture.Attempt.Instance, new(fixture.Attempt.Instance, new()), fixture.Attempt.Database);
        var snapshot = fixture.Registry.ReadSnapshot();
        Assert.Equal(6, snapshot.Journals.Count);
        Assert.Equal(JournalKind.ExtensionApply, snapshot.Journals[^1].Kind);
        evidence.VerifyRecognizedAll(snapshot.Journals);
        using var observation = new DatabaseObservation(fixture.Attempt.Database);
        var before = Fingerprints(fixture.Attempt.Instance.RuntimeRoot);
        var index = new RecoveryIndex();
        Assert.ThrowsAny<Exception>(() => new RecoveryCoordinator(fixture.Registry, evidence, index).RecoverAndPublish(evidence, fixture.Handlers()));
        Assert.False(index.Current.IsReady);
        Assert.Equal(snapshot.Revision, fixture.Registry.ReadSnapshot().Revision);
        Assert.All(fixture.Registry.ReadSnapshot().Journals, journal => Assert.Equal(1, journal.Revision));
        Assert.Null(new AttemptReceiptStore(fixture.Attempt.Database).Read(Guid.Parse(fixture.Attempt.Plan.AttemptId)));
        Assert.Equal(before, Fingerprints(fixture.Attempt.Instance.RuntimeRoot));
    }

    private static string[] Fingerprints(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Where(path => !string.Equals(path, Path.Combine(root, "instance.lock"), StringComparison.OrdinalIgnoreCase))
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(root, path).Replace('\\', '/') + ":" +
            Fingerprint(path)).ToArray();

    private static string Fingerprint(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexStringLower(SHA256.HashData(file));
    }
}
