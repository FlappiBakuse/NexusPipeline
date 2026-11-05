using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class WorkChainTests
{
    [Theory(DisplayName = "Read Plan Rejects An Unaccepted Frozen Chain Even With The Original Actual Sql Claims")]
    [InlineData("unknown-previous")]
    [InlineData("noncontiguous")]
    [InlineData("self-previous")]
    public async Task RequiresAcceptedChains(string change)
    {
        await using var fixture = await WorkCleanupFixture.CreateAsync();
        var owner = fixture.Original.Package.Catalog;
        var accepted = await fixture.RunAsync(Guid.NewGuid());
        var registry = new RecoveryJournalRegistry(owner.Database);
        var registered = registry.ReadJournal(accepted.JournalId);
        Assert.Equal(JournalPhase.Committed, registered.Phase);
        var codec = new CleanupPlanCodec(owner.Instance, owner.Contracts, new(owner.Instance, owner.Contracts),
            owner.Database, fixture.Original.Roots);
        var plan = codec.Read(accepted.Plan); var work = plan.ExtensionWork!;
        Assert.True(work.Page.Indices.Length > 1);
        var page = change switch
        {
            "unknown-previous" => work.Page with { PreviousJournalId = Guid.NewGuid(), PreviousPlan = accepted.Plan },
            "noncontiguous" => work.Page with { Indices = work.Page.Indices.Skip(1).ToImmutableArray() },
            "self-previous" => work.Page with { PreviousJournalId = registered.JournalId, PreviousPlan = accepted.Plan },
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };
        var blobs = new ImmutableBlobStore(owner.Instance, owner.Contracts);
        var pageReference = blobs.PersistPlan(JsonSerializer.SerializeToUtf8Bytes(page,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var candidate = plan with { ExtensionWork = new(pageReference, page, work.Origin) };
        var reference = codec.Persist(candidate);
        var readOnlyCandidate = registered with { PlanPath = reference.RelativePath, PlanDigest = reference.Sha256 };
        Assert.Equal(FileCleanupItems.Keys(plan), FileCleanupItems.Keys(candidate));
        var savedSql = JsonSerializer.Serialize(registry.ReadSnapshot());
        var saved = Evidence(owner.Instance);
        Assert.ThrowsAny<IOException>(() => CleanupJournalBinding.ReadPlan(owner.Instance, owner.Contracts,
            readOnlyCandidate, owner.Database, fixture.Original.Roots));
        Assert.Equal(savedSql, JsonSerializer.Serialize(registry.ReadSnapshot()));
        Assert.Equal(saved, Evidence(owner.Instance));
        Assert.Equal(registered.PlanDigest, registry.ReadJournal(registered.JournalId).PlanDigest);
        Assert.False(Directory.Exists(fixture.Workspace));
    }

    private static string[] Evidence(RuntimeInstance instance) =>
        new[] { Path.Combine(instance.RuntimeRoot, "journals"), Path.Combine(instance.RuntimeRoot, "blobs", "plans") }
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(instance.RuntimeRoot, path) + ":" +
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
}
