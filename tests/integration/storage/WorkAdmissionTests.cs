using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class WorkAdmissionTests
{
    [Theory(DisplayName = "A Committed Work Page Cannot Hide Invalid Earlier Page History At Read Only Admission Or Final Publication")]
    [InlineData("changed")]
    [InlineData("missing")]
    [InlineData("reordered")]
    public async Task RejectsEarlierTampering(string change)
    {
        await using var fixture = await WorkCleanupFixture.CreateAsync();
        var owner = fixture.Original.Package.Catalog;
        var first = await CommitPage(fixture, null, 1);
        var last = await CommitPage(fixture, first, WorkCleanupService.PageNodeCount);
        Assert.False(Directory.Exists(fixture.Workspace));
        var registry = new RecoveryJournalRegistry(owner.Database);
        var registered = registry.ReadJournal(last.JournalId);
        Assert.Equal(JournalPhase.Committed, registered.Phase);
        for (var repeat = 0; repeat < 2; repeat++)
            _ = CleanupJournalBinding.ReadExact(owner.Instance, owner.Contracts, registered,
                new FileEffectOutbox(owner.Database).Read(last.JournalId), owner.Database, fixture.Original.Roots);
        var lastBytes = JournalBytes(owner.Instance, last.JournalId);
        var priorDirectory = Path.Combine(owner.Instance.RuntimeRoot, "journals", first.JournalId.ToString("D"));
        var files = Directory.EnumerateFiles(priorDirectory).Order(StringComparer.Ordinal).ToArray();
        Assert.True(files.Length >= 3);
        byte[]? displaced = null;
        string? preserved = null;
        if (change == "changed")
        {
            displaced = File.ReadAllBytes(files[1]);
            preserved = Path.Combine(fixture.Original.Package.Root, "original-work-revision.bin");
            File.WriteAllBytes(preserved, displaced);
            var document = JsonNode.Parse(displaced)!.AsObject();
            document["receipts"]![0]!["detailCode"] = "cleanup_applied";
            File.WriteAllBytes(files[1], JsonSerializer.SerializeToUtf8Bytes(document));
        }
        else if (change == "missing")
        {
            displaced = File.ReadAllBytes(files[0]);
            preserved = Path.Combine(fixture.Original.Package.Root, "original-work-revision.bin");
            File.Move(files[0], preserved);
        }
        else
        {
            var before = File.ReadAllBytes(files[0]); var after = File.ReadAllBytes(files[1]);
            File.WriteAllBytes(files[0], after); File.WriteAllBytes(files[1], before);
        }
        var physical = AllEvidenceBytes(owner.Instance);
        var sql = JsonSerializer.Serialize(registry.ReadSnapshot());
        Assert.ThrowsAny<IOException>(() => CleanupJournalBinding.ReadPlan(owner.Instance, owner.Contracts,
            registered, owner.Database, fixture.Original.Roots));
        Assert.ThrowsAny<IOException>(() => CleanupJournalBinding.ReadExact(owner.Instance, owner.Contracts,
            registered, new FileEffectOutbox(owner.Database).Read(last.JournalId), owner.Database, fixture.Original.Roots));
        var recovery = new StartupJournalRecovery(owner.Instance, owner.Contracts, owner.Database, fixture.Original.Roots);
        Assert.ThrowsAny<IOException>(() => recovery.InspectBindings(registry.ReadSnapshot()));
        Assert.ThrowsAny<IOException>(() => new RecoveryCoordinator(registry,
            new AdmissionPublicationEvidence(owner.Instance, owner.Database, owner.Contracts, fixture.Original.Roots),
            owner.Index).VerifyAndPublish());
        Assert.False(owner.Index.Current.IsReady);
        Assert.Equal(sql, JsonSerializer.Serialize(registry.ReadSnapshot()));
        Assert.Equal(physical, AllEvidenceBytes(owner.Instance));
        Assert.Equal(lastBytes, JournalBytes(owner.Instance, last.JournalId));
        if (preserved is not null) Assert.Equal(displaced, File.ReadAllBytes(preserved));
    }

    internal static async Task<CleanupAcceptanceResult> CommitPage(WorkCleanupFixture fixture,
        CleanupAcceptanceResult? previous, int maximum)
    {
        var owner = fixture.Original.Package.Catalog;
        var seed = fixture.DirectPage(Guid.NewGuid());
        var work = seed.ExtensionWork!;
        var completed = new ExtensionWorkHistory(owner.Instance, owner.Database, owner.Contracts, fixture.Original.Roots)
            .Completed(work.Origin, previous?.JournalId, previous?.Plan);
        var indices = work.Origin.Proof.Nodes.Select(node => node.Index)
            .Where(index => !completed.Contains(index)).Take(maximum).ToImmutableArray();
        Assert.False(indices.IsDefaultOrEmpty);
        var page = work.Page with { PreviousJournalId = previous?.JournalId, PreviousPlan = previous?.Plan, Indices = indices };
        var codec = new ExtensionWorkCodec(owner.Instance, owner.Database, owner.Contracts, fixture.Original.Roots);
        var plan = seed with { ExtensionWork = codec.ReadPage(codec.PersistPage(page)) };
        CleanupAcceptanceResult accepted;
        using (var root = owner.Resources.TryAcquire(plan.OperationId, FileCleanupItems.Claims(plan)).Lease)
        {
            Assert.NotNull(root);
            using var prepared = await PreparedWorkCleanup.PrepareAsync(owner.Instance, owner.Database, owner.Lease,
                owner.Contracts, fixture.Original.Roots, owner.Resources, fixture.Original.Activator, root, plan, "local", true, default);
            accepted = new FileCleanupAcceptance(owner.Instance, owner.Contracts, owner.Database, payloadRoots: fixture.Original.Roots)
                .AcceptPrepared(prepared.Identity, prepared, DateTimeOffset.UtcNow);
            _ = new OwnedCleanupExecutor(owner.Instance, owner.Contracts, owner.Database, owner.Index)
                .ExecutePrepared(accepted.JournalId, owner.Lease, root, fixture.Original.Roots, prepared.Execution, null);
        }
        ExtensionProjectionPublication.PublishIfDirty(owner.Instance, owner.Database, owner.Lease,
            owner.Contracts, fixture.Original.Roots, owner.Resources, owner.Index);
        Assert.Equal(JournalPhase.Committed, new RecoveryJournalRegistry(owner.Database).ReadJournal(accepted.JournalId).Phase);
        Assert.All(new FileEffectOutbox(owner.Database).Read(accepted.JournalId),
            item => Assert.Equal(FileEffectState.Completed, item.State));
        return accepted;
    }

    private static string[] AllEvidenceBytes(RuntimeInstance instance) =>
        new[] { Path.Combine(instance.RuntimeRoot, "journals"), Path.Combine(instance.RuntimeRoot, "blobs", "plans") }
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(instance.RuntimeRoot, path) + ":" +
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();

    private static string[] JournalBytes(RuntimeInstance instance, Guid id) =>
        Directory.EnumerateFiles(Path.Combine(instance.RuntimeRoot, "journals", id.ToString("D")))
            .Order(StringComparer.Ordinal).Select(path => Path.GetFileName(path) + ":" +
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
}
