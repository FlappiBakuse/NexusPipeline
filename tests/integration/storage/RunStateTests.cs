using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class RunStateTests
{
    [Theory(DisplayName = "Independent Frozen Synchronization Triggers Survive Manifest Reopening")]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void FreezesIndependentTriggers(bool enabled, bool initially, bool atFinish)
    {
        var fixture = Create();
        var state = fixture.State with { AutoSyncEnabled = enabled, SyncInitially = initially, SyncAtFinish = atFinish };
        using var prepared = fixture.Codec.Freeze(state);
        var reopened = fixture.Codec.Read(prepared.Reference);
        Assert.Equal(enabled, reopened.AutoSyncEnabled);
        Assert.Equal(initially, reopened.SyncInitially); Assert.Equal(atFinish, reopened.SyncAtFinish);
        using var again = fixture.Codec.Freeze(reopened);
        Assert.Equal(prepared.Reference, again.Reference);
    }

    [Fact(DisplayName = "Frozen Run State Reopens Initial Choices And The Independent Source Revision Without Reading Any Live Configuration")]
    public void ReopensInitialChoices()
    {
        var fixture = Create();
        using var prepared = fixture.Codec.Freeze(fixture.State);
        var reference = prepared.Reference;
        var targetBefore = File.ReadAllBytes(fixture.Target);
        File.Delete(fixture.SourcePath);
        var reopened = new RunStateCodec(fixture.Instance, new(), fixture.Blobs).Read(reference);
        Assert.Equal(fixture.State.Source.Address, reopened.Source.Address);
        Assert.Equal(fixture.State.Source.Contract, reopened.Source.Contract);
        Assert.Equal(fixture.State.Source.Tree.Entries, reopened.Source.Tree.Entries);
        Assert.Equal(fixture.State.ConfigurationOperationId, reopened.Tasks[0].Initial.Source.OperationId);
        Assert.True(reopened.Tasks[0].Initial.Values["daily"]);
        Assert.Empty(reopened.Tasks[0].Rule.SelectedIds);
        Assert.True(reopened.AutoSyncEnabled);
        using var repeated = fixture.Codec.Freeze(reopened);
        Assert.Equal(reference, repeated.Reference);
        Assert.Equal(targetBefore, File.ReadAllBytes(fixture.Target));
    }

    [Theory(DisplayName = "Invalid Run State Ownership Is Rejected Before Persisting A State Manifest")]
    [InlineData("instance")]
    [InlineData("empty-owner")]
    [InlineData("same-operation")]
    [InlineData("empty-user")]
    [InlineData("binding-revision")]
    public void RejectsInvalidOwners(string fault)
    {
        var fixture = Create();
        var state = fault switch
        {
            "instance" => fixture.State with { InstanceId = Guid.NewGuid() },
            "empty-owner" => fixture.State with { OwnerOperationId = Guid.Empty },
            "same-operation" => fixture.State with { OwnerOperationId = fixture.State.ConfigurationOperationId },
            "empty-user" => fixture.State with { Source = fixture.State.Source with { UserId = Guid.Empty } },
            _ => fixture.State with { BindingRevision = 0 }
        };
        var before = Fingerprints(fixture.Instance.RuntimeRoot);
        Assert.ThrowsAny<Exception>(() => fixture.Codec.Freeze(state));
        Assert.Equal(before, Fingerprints(fixture.Instance.RuntimeRoot));
    }

    [Theory(DisplayName = "Reopening Unknown Or Forged Frozen Task Facts Preserves Every Input Byte And Does Not Create Content")]
    [InlineData("initial-value")]
    [InlineData("duplicate-id")]
    [InlineData("unknown-version")]
    [InlineData("old-path")]
    [InlineData("noncanonical-id")]
    public void PreservesUnknownFacts(string fault)
    {
        var fixture = Create();
        using var prepared = fixture.Codec.Freeze(fixture.State);
        using var input = fixture.Blobs.OpenVerified(prepared.Reference);
        var json = JsonNode.Parse(input)!.AsObject();
        switch (fault)
        {
            case "initial-value": json["tasks"]![0]!["initialValues"]![0]!["enabled"] = false; break;
            case "duplicate-id": json["tasks"]![0]!["initialValues"]!.AsArray().Add(json["tasks"]![0]!["initialValues"]![0]!.DeepClone()); break;
            case "unknown-version": json["version"] = 2; break;
            case "old-path": json["tasks"]![0]!["declaration"]!["source"]!["items"] = "tasks"; break;
            case "noncanonical-id": json["source"]!["userId"] = fixture.State.Source.UserId.ToString("D").ToUpperInvariant(); break;
        }
        var invalid = fixture.Blobs.PersistPlan(JsonSerializer.SerializeToUtf8Bytes(json));
        var before = Fingerprints(fixture.Instance.RuntimeRoot);
        Assert.ThrowsAny<Exception>(() => fixture.Codec.Read(invalid));
        Assert.Equal(before, Fingerprints(fixture.Instance.RuntimeRoot));
    }

    [Fact(DisplayName = "Prepared Run State Holds Original Content And Manifests Until Its Consumer Has Accepted The Plan")]
    public void HoldsOriginalContent()
    {
        var fixture = Create();
        var prepared = fixture.Codec.Freeze(fixture.State);
        var content = fixture.State.Source.Tree.Entries.Single();
        var paths = new[] { Path.Combine(fixture.Instance.RuntimeRoot, prepared.Reference.RelativePath),
            Path.Combine(fixture.Instance.RuntimeRoot, $"blobs/content/{content.Sha256}.bin") };
        foreach (var path in paths) Assert.Throws<IOException>(() => File.Delete(path));
        prepared.Dispose(); prepared.Dispose();
        Assert.Throws<ObjectDisposedException>(() => prepared.Reference);
        foreach (var path in paths) { using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); Assert.True(writer.Length > 0); }
    }

    [Theory(DisplayName = "Configuration Exchange Rejects Unbound Run State And An Untransformed Candidate Without Writing Content")]
    [InlineData("wrong-operation")]
    [InlineData("manual-edit")]
    [InlineData("unselected-candidate")]
    public void RejectsUntransformedCandidates(string fault)
    {
        var fixture = Create();
        using var state = fixture.Codec.Freeze(fixture.State);
        var candidate = new ConfigurationTaskTransformation(fixture.Blobs).Select(fixture.State.ConfigurationOperationId, fixture.State.Source, fixture.State.Tasks);
        var plan = Exchange(fixture, state.Reference, candidate);
        plan = fault switch
        {
            "wrong-operation" => plan with { OperationId = Guid.NewGuid() },
            "manual-edit" => plan with { Kind = JournalKind.ConfigEdit },
            _ => plan with { Candidate = Archive(fixture.State.Source.Tree) }
        };
        var before = Fingerprints(fixture.Instance.RuntimeRoot);
        Assert.ThrowsAny<Exception>(() => new ConfigurationPlanCodec(fixture.Instance, new(), fixture.Blobs).Freeze(plan));
        Assert.Equal(before, Fingerprints(fixture.Instance.RuntimeRoot));
    }

    [Fact(DisplayName = "Accepted Run Exchange Retains The Unselected Original Snapshot Under The Actual Content Deletion Guard")]
    public void ProtectsUnselectedSnapshots()
    {
        var fixture = Create();
        BlobReference reference; FrozenConfigurationExchange plan;
        using (var state = fixture.Codec.Freeze(fixture.State))
        {
            var selected = new ConfigurationTaskTransformation(fixture.Blobs).Select(fixture.State.ConfigurationOperationId, fixture.State.Source, fixture.State.Tasks);
            using var prepared = new ConfigurationPlanCodec(fixture.Instance, new(), fixture.Blobs).Freeze(Exchange(fixture, state.Reference, selected));
            reference = prepared.Reference; plan = prepared.Plan;
            var journal = new RegisteredJournal(plan.JournalId, plan.OperationId, plan.Kind, JournalPhase.Prepared, 1, reference.RelativePath, reference.Sha256, ["fixture:run-config"]);
            new RecoveryJournalRegistry(fixture.Database).AcceptPrepared(new(plan.OperationId, "fixture", reference.Sha256, "configuration.exchange"), journal, [], DateTimeOffset.UtcNow);
        }
        var entry = fixture.State.Source.Tree.Entries.Single();
        var content = new BlobReference($"blobs/content/{entry.Sha256}.bin", entry.SizeBytes, entry.Sha256!);
        Assert.DoesNotContain(plan.Before.Contents.Concat(plan.Candidate.Contents), item => item.Sha256 == content.Sha256);
        var cleanup = new BlobCleanup(fixture.Instance, fixture.Blobs, new(fixture.Instance, new(), fixture.Database, fixture.Blobs));
        var candidates = new CleanupPlanCodec(fixture.Instance, new(), fixture.Blobs).Capture(Guid.NewGuid(), Guid.NewGuid(), [content]);
        Assert.Equal(CleanupStepResult.SkippedReferenced, cleanup.RemoveVerified(Assert.Single(candidates.Items)));
        fixture.Blobs.Verify(content);
        Assert.Equal("external original", File.ReadAllText(fixture.Target));
    }

    [Fact(DisplayName = "Explicit Task Maps Reopen From Canonical Run State Without Reading Live Configuration")]
    public void ReopensExplicitTargets()
    {
        var fixture = Create();
        var rule = fixture.State.Tasks[0].Rule with { Declaration = new(new ConfigurationSelectorTasks(
            [new("plugin:daily", new([new ConfigurationProperty("tasks"), new ConfigurationProperty("daily")]))]), ["plugin:daily"]) };
        var tasks = new ConfigurationTaskTransformation(fixture.Blobs).Capture(fixture.State.ConfigurationOperationId, fixture.State.Source, [rule]);
        using var prepared = fixture.Codec.Freeze(fixture.State with { Tasks = tasks });
        File.Delete(fixture.SourcePath);
        var reopened = fixture.Codec.Read(prepared.Reference);
        var source = Assert.IsType<ConfigurationSelectorTasks>(reopened.Tasks[0].Rule.Declaration.Source);
        Assert.Equal("plugin:daily", Assert.Single(source.Targets).TaskId);
        Assert.True(reopened.Tasks[0].Initial.Values["plugin:daily"]);
        using var repeated = fixture.Codec.Freeze(reopened);
        Assert.Equal(prepared.Reference, repeated.Reference);
        var selected = new ConfigurationTaskTransformation(fixture.Blobs).Select(reopened.ConfigurationOperationId, reopened.Source, reopened.Tasks);
        Assert.NotEqual(reopened.Source.Tree.Entries[0].Sha256, selected.Entries[0].Sha256);
        Assert.Equal("external original", File.ReadAllText(fixture.Target));
    }

    private static FrozenConfigurationExchange Exchange(Fixture fixture, BlobReference state, FileTreeSnapshot candidate) => new(
        fixture.Instance.InstanceId, fixture.State.ConfigurationOperationId, Guid.NewGuid(), JournalKind.ConfigExchange, fixture.Target,
        PhysicalObjectIdentity.Read(fixture.Root), new FileTreeArchive(new(SnapshotBudget.Default), fixture.Blobs).Capture(fixture.Target, default), Archive(candidate)) { RunState = state };

    private static ArchivedFileTree Archive(FileTreeSnapshot tree) => new(tree, [], tree.Entries.Where(item => item.Kind == FileObjectKind.File)
        .Select(item => new BlobReference($"blobs/content/{item.Sha256}.bin", item.SizeBytes, item.Sha256!)).Distinct().ToArray());

    private static Fixture Create()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "run-state-" + Guid.NewGuid().ToString("N"));
        var instance = new InstanceRoot(new()).OpenOrCreate(Path.Combine(root, "runtime"));
        var database = StateDatabase.CreateFresh(instance); var blobs = new ImmutableBlobStore(instance, new());
        var sourcePath = Path.Combine(root, "source.json"); File.WriteAllText(sourcePath, "{\"tasks\":{\"daily\":true},\"count\":7}");
        var tree = new FileTreeArchive(new(SnapshotBudget.Default), blobs).Capture(sourcePath, default).Snapshot;
        var source = new ConfigurationRevision(new(Guid.NewGuid(), "main"), 3, Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), Guid.NewGuid(), new(1, new('a', 64), null, false), tree);
        var selector = new ConfigurationSelector([new ConfigurationProperty("tasks")]);
        var access = new ConfigurationDocumentAccess([new(selector, true)], [new(selector, true)], []);
        var rule = new RunTaskRule("", ConfigurationSyntax.Json, access, new(new ConfigurationMapTasks(selector), ["daily"]), []);
        var configurationOperationId = Guid.NewGuid();
        var tasks = new ConfigurationTaskTransformation(blobs).Capture(configurationOperationId, source, [rule]);
        var state = new FrozenRunState(instance.InstanceId, Guid.NewGuid(), configurationOperationId, source, 2, true, tasks);
        var target = Path.Combine(root, "external.json"); File.WriteAllText(target, "external original");
        return new(root, sourcePath, target, instance, database, blobs, state, new(instance, new(), blobs));
    }

    private static string[] Fingerprints(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))).ToArray();

    private sealed record Fixture(string Root, string SourcePath, string Target, RuntimeInstance Instance, StateDatabase Database,
        ImmutableBlobStore Blobs, FrozenRunState State, RunStateCodec Codec);
}
