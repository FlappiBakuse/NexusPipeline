using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class SecretServiceTests
{
    [Fact(DisplayName = "Concurrent Secret Use Case Replays Wait For The Same Accepted Preparation Without Repeating Ciphertext Writes")]
    public async Task SharesConcurrentReplays()
    {
        using var fixture = SecretTestRuntime.Create();
        const string plaintext = "secret-service-replay-fixture";
        using var value = new SecretValue(plaintext);
        var operation = Guid.NewGuid();
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
            await fixture.Service.ApplyAsync(operation, fixture.Key, 0, SecretDisposition.Set, value, default))));
        Assert.All(results, result => Assert.Equal(results[0], result));
        Assert.Equal(1, results[0].Revision);
        using var recovered = await fixture.Service.ReadAsync(fixture.Key, default);
        recovered.Use(chars => Assert.True(chars.SequenceEqual(plaintext)));
        Assert.Single(Directory.GetFiles(Path.Combine(fixture.Instance.RuntimeRoot, "secrets/payloads")));
        Assert.Equal(JournalPhase.Committed, Assert.Single(new RecoveryJournalRegistry(fixture.Database).ReadSnapshot().Journals).Phase);
        Assert.True(fixture.Index.Current.IsReady);
        Assert.Empty(fixture.Index.Current.Isolations);
    }

    [Fact(DisplayName = "The Secret Use Case Retires Old Ciphertext Using Actual Shared Leases And Publishes Its Closed Recovery State")]
    public async Task ClosesOwnedRetirement()
    {
        using var fixture = SecretTestRuntime.Create();
        using var value = new SecretValue("secret-service-fixture");
        var operation = Guid.NewGuid();
        var first = await fixture.Service.ApplyAsync(operation, fixture.Key, 0, SecretDisposition.Set, value, default);
        var oldPath = Assert.Single(Directory.GetFiles(Path.Combine(fixture.Instance.RuntimeRoot, "secrets/payloads")));
        var cleared = await fixture.Service.ApplyAsync(Guid.NewGuid(), fixture.Key, 1, SecretDisposition.Clear, null, default);
        Assert.False(cleared.IsConfigured);
        Assert.False(File.Exists(oldPath));
        Assert.Equal(JournalPhase.Committed, Assert.Single(new RecoveryJournalRegistry(fixture.Database).ReadSnapshot().Journals, value => value.Kind == JournalKind.FileCleanup).Phase);
        Assert.Equal(first, await fixture.Service.ApplyAsync(operation, fixture.Key, 0, SecretDisposition.Set, value, default));
        Assert.False((await fixture.Service.GetStateAsync(fixture.Key, default)).IsConfigured);
        Assert.True(fixture.Index.Current.IsReady);
        Assert.Empty(fixture.Index.Current.Isolations);
        Assert.NotNull(fixture.Lifecycle.RequestClose(Guid.NewGuid(), Guid.NewGuid()).Permit);
    }

    [Theory(DisplayName = "A Secret Use Case Cannot Create Ciphertext Outside Its Live Instance And Shared Admission")]
    [InlineData("conflict")]
    [InlineData("recovery")]
    [InlineData("closing")]
    [InlineData("expired-instance")]
    public async Task RequiresLiveAdmission(string fault)
    {
        using var fixture = SecretTestRuntime.Create();
        using var conflict = fault == "conflict"
            ? fixture.Resources.TryAcquire(Guid.NewGuid(), [new("secrets:fields", ResourceClaimMode.Mutation)]).Lease : null;
        if (fault == "recovery") fixture.Index.BlockUnknown("fixture_unknown");
        if (fault == "closing") fixture.Lifecycle.RequestClose(Guid.NewGuid(), Guid.NewGuid());
        if (fault == "expired-instance") fixture.Lease.Dispose();
        using var value = new SecretValue("secret-service-fixture");
        var failure = await Record.ExceptionAsync(async () =>
            await fixture.Service.ApplyAsync(Guid.NewGuid(), fixture.Key, 0, SecretDisposition.Set, value, default));
        Assert.NotNull(failure);
        if (fault != "expired-instance") Assert.Equal(fault == "conflict" ? "secret_resource_conflict" : "secret_host_unavailable", failure.Message);
        Assert.False(Directory.Exists(Path.Combine(fixture.Instance.RuntimeRoot, "secrets/payloads")));
        Assert.False((await fixture.Store.GetStateAsync(fixture.Key, default)).IsConfigured);
    }
}
