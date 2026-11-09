using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.TestFixtures;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class SnapshotPrototypeTests
{
    [Fact(DisplayName = "Damaged Snapshot Bytes Are Rejected Before Creating A Restored Instance")]
    public async Task RejectsDamagedSnapshots()
    {
        var parent = Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new IOException("Missing external test root");
        var root = Path.Combine(parent, "snapshot-" + Guid.NewGuid().ToString("N"));
        var contracts = new ContractReader();
        var instance = new InstanceRoot(contracts).OpenOrCreate(Path.Combine(root, "source-runtime"));
        var database = StateDatabase.CreateFresh(instance);
        var key = new SecretFieldKey("fixture-extension", Guid.NewGuid(), "credential");
        using (var secret = new SecretValue("snapshot-secret-fixture-018"))
            await new SecretFieldStore(instance, database).ApplyAsync(Guid.NewGuid(), key, 0, SecretDisposition.Set, secret, default);
        using var lease = InstanceLease.Acquire(instance, contracts);
        var artifact = Path.Combine(root, "snapshot");
        var files = FixtureInstanceSnapshot.Create(instance, database, artifact);
        var entry = files.First(item => item.RelativePath.StartsWith("secrets/", StringComparison.Ordinal));
        var path = Path.Combine(artifact, entry.RelativePath);
        var damaged = File.ReadAllBytes(path);
        damaged[0] ^= 1;
        File.WriteAllBytes(path, damaged);
        var target = Path.Combine(root, "must-not-exist");
        Assert.Throws<IOException>(() => FixtureInstanceSnapshot.Restore(artifact, target, files));
        Assert.False(Directory.Exists(target));
        Assert.Equal(damaged, File.ReadAllBytes(path));
    }
}
