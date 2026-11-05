using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class OperationBoundaryTests
{
    [Fact(DisplayName = "Real Removal Reports Its Own Committed Outcome And Keeps The Earlier Installation Historical")]
    public async Task PreservesRemovalOutcome()
    {
        await using var fixture = await ExtensionUpgradeFixture.CreateAsync(enabled: true);
        var owner = fixture.Original.Package.Catalog;
        var queries = new ExtensionQueryService(owner.Instance, owner.Lease, owner.Database,
            owner.Contracts, fixture.Original.Roots);
        var original = queries.Read(fixture.Original.Package.OperationId, "local")!;
        Assert.Equal(ExtensionApplyOutcome.Installed, original.Outcome); Assert.NotNull(original.HistoricalActivationId);
        fixture.Prepare(PayloadAction.Remove); _ = fixture.Accept();
        Assert.Equal(ExtensionApplyOutcome.Pending, queries.Read(fixture.Next.OperationId, "local")!.Outcome);
        _ = await fixture.Session().ApplyAsync(fixture.Prepared!, default);
        var removed = queries.Read(fixture.Next.OperationId, "local")!;
        Assert.Equal(ExtensionApplyOutcome.Removed, removed.Outcome); Assert.Equal(2, removed.ResultInstalledRevision);
        Assert.False(removed.ResultIsInstalled); Assert.Null(removed.HistoricalActivationId);
        Assert.Equal(original, queries.Read(fixture.Original.Package.OperationId, "local"));
        Assert.False(fixture.Original.Activator.IsPublished("fixture-plugin", original.HistoricalActivationId!.Value));
        Assert.False(Directory.Exists(fixture.Original.Target));
    }

    [Theory(DisplayName = "An Actually Stopped Unknown Activation Remains Pending And A Query Never Completes Or Replays It")]
    [InlineData("applied")] [InlineData("callback")]
    public async Task KeepsUnknownActivation(string point)
    {
        await using var fixture = new InstallationCrashFixture(point);
        await fixture.StartAsync(); Assert.True((await fixture.StopAsync()).CompleteJobStopped);
        using var lease = InstanceLease.Acquire(fixture.Instance, new());
        var database = StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Package.Root, "inspection"),
            new JournalStartupAdmission(fixture.Instance, new(), fixture.Roots));
        var before = fixture.History(); var effects = new FileEffectOutbox(database).Read(fixture.JournalId).ToArray();
        var queries = new ExtensionQueryService(fixture.Instance, lease, database, new(), fixture.Roots);
        var state = queries.Read(fixture.OperationId, "local:installation-owner")!;
        Assert.Equal(ExtensionApplyOutcome.Pending, state.Outcome); Assert.Equal(JournalPhase.Applied, state.Phase);
        Assert.Equal("accepted", state.Operation.Status); Assert.Null(state.ResultInstalledRevision);
        Assert.Null(state.ResultIsInstalled); Assert.Null(state.HistoricalActivationId);
        Assert.Equal(state, queries.Read(fixture.OperationId, "local:installation-owner"));
        var after = fixture.History(); Assert.Equal(before.Length, after.Length);
        for (var index = 0; index < before.Length; index++) Assert.Equal(before[index], after[index]);
        Assert.Equal(effects, new FileEffectOutbox(database).Read(fixture.JournalId).ToArray());
        Assert.False(File.Exists(Path.Combine(fixture.ActivationDirectory, "cleaned.json")));
    }
}
