using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class InitialRecoveryTests
{
    [Fact(DisplayName = "An Accepted Initial Configuration Without A File Journal Requires Positive No Effect Evidence And Replays As Completed")]
    public void ProvesUnenteredAcceptance()
    {
        var fixture = Create();
        var before = Fingerprints(fixture.Instance.RuntimeRoot);
        var evidence = new JournalRecoveryEvidence(fixture.Instance, new(fixture.Instance, fixture.Contracts));
        evidence.VerifyRecognizedAll(fixture.Registry.ReadSnapshot().Journals);
        var work = fixture.Handler.Prepare(fixture.Registered);
        Assert.Equal(before, Fingerprints(fixture.Instance.RuntimeRoot));
        Assert.Equal(JournalRecoveryResult.NotExecuted, work.Execute());
        var current = Assert.Single(fixture.Registry.ReadSnapshot().Journals);
        Assert.Equal(JournalPhase.Committed, current.Phase);
        Assert.Empty(fixture.Registry.ReadSnapshot().Isolations);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(fixture.Target));
        var committed = Fingerprints(fixture.Instance.RuntimeRoot);
        Assert.Equal(JournalRecoveryResult.AlreadyCompleted, fixture.Handler.Prepare(current).Execute());
        Assert.Equal(committed, Fingerprints(fixture.Instance.RuntimeRoot));
    }

    [Theory(DisplayName = "An Unproven Initial Site Gets A Recognized Isolation Without Moving Or Overwriting Any Configuration")]
    [InlineData("changed-target")]
    [InlineData("unknown-workspace")]
    [InlineData("unknown-writers")]
    public void IsolatesUnprovenSites(string fault)
    {
        var fixture = Create(unknownWriters: fault == "unknown-writers");
        if (fault == "changed-target") File.WriteAllText(fixture.Target, "unknown target bytes");
        var workPath = Path.Combine(Path.GetDirectoryName(fixture.Target)!, ".nexus-" + fixture.Registered.JournalId.ToString("D"));
        if (fault == "unknown-workspace") { Directory.CreateDirectory(workPath); File.WriteAllText(Path.Combine(workPath, "foreign.data"), "preserve"); }
        var beforeTarget = File.ReadAllBytes(fixture.Target);
        Assert.Equal(JournalRecoveryResult.Quarantined, fixture.Handler.Prepare(fixture.Registered).Execute());
        Assert.Equal(beforeTarget, File.ReadAllBytes(fixture.Target));
        Assert.Equal(JournalPhase.Quarantined, Assert.Single(fixture.Registry.ReadSnapshot().Journals).Phase);
        Assert.Single(fixture.Registry.ReadSnapshot().Isolations);
        if (fault == "unknown-workspace") Assert.Equal("preserve", File.ReadAllText(Path.Combine(workPath, "foreign.data")));
    }

    [Theory(DisplayName = "Unknown Or Unbound Initial Evidence Is Rejected Before Creating A Replacement Journal")]
    [InlineData("unknown-version")]
    [InlineData("unbound-operation")]
    [InlineData("started-effect")]
    public void RejectsUnboundEvidence(string fault)
    {
        var fixture = Create(fault: fault);
        var before = Fingerprints(fixture.Instance.RuntimeRoot);
        Assert.ThrowsAny<Exception>(() => fixture.Handler.Prepare(fixture.Registered));
        Assert.Equal(before, Fingerprints(fixture.Instance.RuntimeRoot));
        Assert.False(Directory.Exists(Path.Combine(fixture.Instance.RuntimeRoot, "journals")));
        Assert.Equal(JournalPhase.Prepared, Assert.Single(fixture.Registry.ReadSnapshot().Journals).Phase);
    }

    [Fact(DisplayName = "An Existing Empty Initial Directory Is Unrecognized Evidence And Cannot Be Filled In As An Absent Journal")]
    public void RejectsExistingDirectories()
    {
        var fixture = Create();
        var directory = Path.Combine(fixture.Instance.RuntimeRoot, "journals", fixture.Registered.JournalId.ToString("D"));
        Directory.CreateDirectory(directory);
        var evidence = new JournalRecoveryEvidence(fixture.Instance, new(fixture.Instance, fixture.Contracts));
        Assert.Throws<IOException>(() => evidence.VerifyRecognizedAll(fixture.Registry.ReadSnapshot().Journals));
        Assert.Throws<IOException>(() => fixture.Handler.Prepare(fixture.Registered));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
    }

    private static Fixture Create(string? fault = null, bool unknownWriters = false)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, Guid.NewGuid().ToString("N"));
        var contracts = new ContractReader();
        var instance = new InstanceRoot(contracts).OpenOrCreate(Path.Combine(root, "runtime"));
        var database = StateDatabase.CreateFresh(instance);
        var blobs = new ImmutableBlobStore(instance, contracts);
        var target = Path.Combine(root, "target.config");
        var candidate = Path.Combine(root, "input.config");
        File.WriteAllBytes(target, [1, 2, 3]);
        File.WriteAllBytes(candidate, [4, 5]);
        var scanner = new FileTreeScanner(SnapshotBudget.Default);
        var archive = new FileTreeArchive(scanner, blobs);
        var plan = new FrozenConfigurationExchange(instance.InstanceId, Guid.NewGuid(), Guid.NewGuid(), JournalKind.ConfigExchange,
            target, PhysicalObjectIdentity.Read(root), archive.Capture(target, CancellationToken.None), archive.Capture(candidate, CancellationToken.None));
        using var preparedPlan = new ConfigurationPlanCodec(instance, contracts, blobs).Freeze(plan);
        var reference = preparedPlan.Reference;
        if (fault is "unknown-version" or "unbound-operation")
        {
            var changed = JsonNode.Parse(File.ReadAllBytes(Path.Combine(instance.RuntimeRoot, reference.RelativePath)))!.AsObject();
            if (fault == "unknown-version") changed["version"] = 17;
            else changed["operationId"] = Guid.NewGuid().ToString("D");
            reference = blobs.PersistPlan(JsonSerializer.SerializeToUtf8Bytes(changed));
        }
        var registered = new RegisteredJournal(plan.JournalId, plan.OperationId, plan.Kind, JournalPhase.Prepared, 1,
            reference.RelativePath, reference.Sha256, ["configuration:fixture"]);
        var registry = new RecoveryJournalRegistry(database);
        registry.AcceptPrepared(new(plan.OperationId, "fixture", reference.Sha256, "configuration.exchange"), registered,
            [new(plan.JournalId, "install-candidate", FileEffectAction.InstallCandidate, FileEffectState.Pending, reference.Sha256, null)], DateTimeOffset.UtcNow);
        if (fault == "started-effect") new FileEffectOutbox(database).Record(plan.JournalId, "install-candidate", FileEffectState.Pending, FileEffectState.Started, new string('d', 64));
        var handler = new ConfigurationRecoveryHandler(instance, contracts, database, plan.Kind,
            id => new(instance.InstanceId, id, !unknownWriters, unknownWriters ? WriterState.Unknown : WriterState.Stopped));
        return new(instance, contracts, registered, registry, handler, target);
    }

    private static string[] Fingerprints(string root)
    {
        var database = Path.Combine(root, "state", "nexus.db");
        // SQLite readers can maintain the shared-memory index and create an empty WAL without changing persistent pages.
        return Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !StringComparer.OrdinalIgnoreCase.Equals(path, database + "-shm") &&
                !(StringComparer.OrdinalIgnoreCase.Equals(path, database + "-wal") && new FileInfo(path).Length == 0))
            .Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
    }
    private sealed record Fixture(RuntimeInstance Instance, ContractReader Contracts, RegisteredJournal Registered,
        RecoveryJournalRegistry Registry, ConfigurationRecoveryHandler Handler, string Target);
}
