using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class SecretRetirementTests
{
    [Theory(DisplayName = "Secret Retirement Deletes Only The Old Ciphertext And Keeps Accepted Replay After Clear Or Replacement")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetiresExactCiphertext(bool replace)
    {
        using var fixture = SecretTestRuntime.Create();
        var original = Guid.NewGuid();
        using var value = new SecretValue("secret-retirement-fixture");
        var first = await fixture.Store.ApplyAsync(original, fixture.Key, 0, SecretDisposition.Set, value, default);
        var oldPath = Assert.Single(Directory.GetFiles(Path.Combine(fixture.Instance.RuntimeRoot, "secrets/payloads")));
        using var replacement = replace ? new SecretValue("replacement-fixture") : null;
        var next = await fixture.Store.ApplyAsync(Guid.NewGuid(), fixture.Key, 1,
            replace ? SecretDisposition.Set : SecretDisposition.Clear, replacement, default);
        var registered = Assert.Single(new RecoveryJournalRegistry(fixture.Database).ReadSnapshot().Journals, value => value.Kind == JournalKind.FileCleanup);
        Assert.Equal("secret-payload:" + first.SecretId!.Value.ToString("D"), Assert.Single(registered.ResourceKeys));
        Assert.Equal(FileCleanupResult.Cleaned, Execute(fixture, registered));
        Assert.False(File.Exists(oldPath));
        Assert.Equal(next, await fixture.Store.GetStateAsync(fixture.Key, default));
        Assert.Equal(first, await fixture.Store.ApplyAsync(original, fixture.Key, 0, SecretDisposition.Set, value, default));
        Assert.True(fixture.Index.Current.IsReady);
        Assert.Empty(fixture.Index.Current.Isolations);
        Assert.Single(Directory.GetFiles(Path.Combine(fixture.Instance.RuntimeRoot, "secrets/keys")));
        if (replace)
        {
            using var observed = await fixture.Store.ReadAsync(fixture.Key, default);
            observed.Use(chars => Assert.True(chars.SequenceEqual("replacement-fixture")));
        }
    }

    [Fact(DisplayName = "Changed Retirement Bytes Are Isolated Without Deleting Their Replacement")]
    public async Task PreservesChangedBytes()
    {
        using var fixture = SecretTestRuntime.Create();
        using var value = new SecretValue("secret-retirement-fixture");
        await fixture.Store.ApplyAsync(Guid.NewGuid(), fixture.Key, 0, SecretDisposition.Set, value, default);
        var oldPath = Assert.Single(Directory.GetFiles(Path.Combine(fixture.Instance.RuntimeRoot, "secrets/payloads")));
        await fixture.Store.ApplyAsync(Guid.NewGuid(), fixture.Key, 1, SecretDisposition.Clear, null, default);
        File.WriteAllBytes(oldPath, [18, 0, 4]);
        var registered = Assert.Single(new RecoveryJournalRegistry(fixture.Database).ReadSnapshot().Journals, value => value.Kind == JournalKind.FileCleanup);
        Assert.Equal(FileCleanupResult.Quarantined, Execute(fixture, registered));
        Assert.Equal(new byte[] { 18, 0, 4 }, File.ReadAllBytes(oldPath));
        Assert.Equal(registered.ResourceKeys.Single(), Assert.Single(fixture.Index.Current.Isolations).ResourceKey);
    }

    [Fact(DisplayName = "A Current Secret Reference Prevents Even An Exactly Bound Cleanup Plan From Removing Its Ciphertext")]
    public async Task PreservesCurrentReferences()
    {
        using var fixture = SecretTestRuntime.Create();
        using var value = new SecretValue("secret-retirement-fixture");
        var state = await fixture.Store.ApplyAsync(Guid.NewGuid(), fixture.Key, 0, SecretDisposition.Set, value, default);
        var path = Assert.Single(Directory.GetFiles(Path.Combine(fixture.Instance.RuntimeRoot, "secrets/payloads")));
        var bytes = File.ReadAllBytes(path);
        var reference = new BlobReference(Path.GetRelativePath(fixture.Instance.RuntimeRoot, path).Replace('\\', '/'), bytes.Length,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
        var operation = new OperationIdentity(Guid.NewGuid(), "fixture", new string('a', 64), "fixture.secret-cleanup");
        var plan = new CleanupPlanCodec(fixture.Instance, new(), new(fixture.Instance, new())).Capture(operation.OperationId, Guid.NewGuid(), [reference]);
        new FileCleanupAcceptance(fixture.Instance, new(), fixture.Database).Accept(operation, plan, DateTimeOffset.UtcNow);
        var registered = Assert.Single(new RecoveryJournalRegistry(fixture.Database).ReadSnapshot().Journals, value => value.Kind == JournalKind.FileCleanup);
        Assert.Equal(FileCleanupResult.Cleaned, Execute(fixture, registered));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(state, await fixture.Store.GetStateAsync(fixture.Key, default));
    }

    [Fact(DisplayName = "Clearing Already Unknown Ciphertext Registers Unverified Retirement Without Granting A Deletion Plan")]
    public async Task IsolatesUnknownCiphertext()
    {
        using var fixture = SecretTestRuntime.Create();
        using var value = new SecretValue("secret-retirement-fixture");
        await fixture.Store.ApplyAsync(Guid.NewGuid(), fixture.Key, 0, SecretDisposition.Set, value, default);
        var path = Assert.Single(Directory.GetFiles(Path.Combine(fixture.Instance.RuntimeRoot, "secrets/payloads")));
        File.WriteAllBytes(path, [18, 0, 4]);
        var state = await fixture.Store.ApplyAsync(Guid.NewGuid(), fixture.Key, 1, SecretDisposition.Clear, null, default);
        Assert.False(state.IsConfigured);
        Assert.Equal(new byte[] { 18, 0, 4 }, File.ReadAllBytes(path));
        Assert.DoesNotContain(new RecoveryJournalRegistry(fixture.Database).ReadSnapshot().Journals, value => value.Kind == JournalKind.FileCleanup);
        using var connection = new SqliteConnection($"Data Source={fixture.Database.DatabasePath};Pooling=False;Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT state FROM secret_retirements WHERE journal_id IS NULL;";
        Assert.Equal("unverified", command.ExecuteScalar());
    }

    private static FileCleanupResult Execute(SecretTestRuntime fixture, RegisteredJournal registered)
    {
        using var claim = fixture.Resources.TryAcquire(registered.OperationId,
            registered.ResourceKeys.Select(key => new ResourceClaim(key, ResourceClaimMode.Mutation)).ToArray()).Lease!;
        return new OwnedCleanupExecutor(fixture.Instance, new(), fixture.Database, fixture.Index).Execute(registered.JournalId, fixture.Lease, claim);
    }

}
