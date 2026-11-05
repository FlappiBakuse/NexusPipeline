using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class RunBindingTests
{
    [Theory(DisplayName = "Actual Parent Attempt Freezes And Reopens Its Own Account Configuration And Selected Task Copy")]
    [InlineData(false)]
    [InlineData(true)]
    public void FreezesSelectedConfiguration(bool tasks)
    {
        var fixture = Create(tasks);
        using var prepared = Codec(fixture).Freeze(fixture.Attempt.Plan);
        var reopened = Codec(fixture).Read(prepared.Reference);
        Assert.Equal(prepared.Plan.Export(), reopened.Export());
        var child = new ConfigurationPlanCodec(fixture.Attempt.Instance, new(), fixture.Attempt.Source.Blobs).Read(fixture.ChildReference);
        Assert.NotNull(child.RunState);
        var state = new RunStateCodec(fixture.Attempt.Instance, new(), fixture.Attempt.Source.Blobs).Read(child.RunState!);
        Assert.Equal(fixture.State.Source.Address, state.Source.Address);
        Assert.Equal(fixture.Attempt.Source.Plan.Steps[0].UserId, state.Source.UserId.ToString("D"));
        Assert.Equal(fixture.OriginalTarget, Fingerprints(fixture.Attempt.Configuration.TargetPath));
    }

    [Theory(DisplayName = "Actual Parent Attempt Rejects Cross Account Or Unselected Run Configuration Without Changing Any Input Byte")]
    [InlineData("owner")]
    [InlineData("binding")]
    [InlineData("user")]
    [InlineData("script")]
    [InlineData("binding-revision")]
    [InlineData("selected-ids")]
    [InlineData("undeclared-task")]
    public void RejectsForeignConfiguration(string fault)
    {
        var fixture = Create(fault is "selected-ids" or "undeclared-task", fault);
        var before = Fingerprints(fixture.Attempt.Root);
        var error = Assert.Throws<IOException>(() => Codec(fixture).Freeze(fixture.Attempt.Plan));
        Assert.Equal(fault is "selected-ids" or "undeclared-task"
            ? "run_attempt_configuration_tasks_unbound" : "run_attempt_configuration_owner_unbound", error.Message);
        Assert.Equal(before, Fingerprints(fixture.Attempt.Root));
    }

    [Fact(DisplayName = "Accepted Parent Attempt Protects Initial Task Content Under The Actual Native Deletion Guard")]
    public void ProtectsInitialContent()
    {
        var fixture = Create(tasks: true);
        fixture.Attempt.Accept();
        var entry = fixture.State.Source.Tree.Entries.Single(item => item.Kind == FileObjectKind.File);
        var source = new BlobReference($"blobs/content/{entry.Sha256}.bin", entry.SizeBytes, entry.Sha256!);
        Assert.DoesNotContain(fixture.Child.Before.Contents.Concat(fixture.Child.Candidate.Contents), item => item.Sha256 == source.Sha256);
        var database = fixture.Attempt.Database; var instance = fixture.Attempt.Instance; var blobs = fixture.Attempt.Source.Blobs;
        var journals = new RecoveryJournalRegistry(database).ReadSnapshot().Journals;
        Assert.Contains(journals, item => item.JournalId == fixture.Attempt.JournalId && item.Kind == JournalKind.RunAttempt);
        Assert.DoesNotContain(journals, item => item.JournalId == fixture.Child.JournalId);
        var cleanup = new BlobCleanup(instance, blobs, new(instance, new(), database, blobs));
        var candidates = new CleanupPlanCodec(instance, new(), blobs).Capture(Guid.NewGuid(), Guid.NewGuid(), [source]);
        var candidate = Assert.Single(candidates.Items);
        Assert.Equal(CleanupItemState.Referenced, cleanup.Inspect(candidate));
        Assert.Equal(CleanupStepResult.SkippedReferenced, cleanup.RemoveVerified(candidate));
        blobs.Verify(source);
        Assert.Equal(fixture.OriginalTarget, Fingerprints(fixture.Attempt.Configuration.TargetPath));
    }

    private static AttemptPlanCodec Codec(Fixture fixture) => new(fixture.Attempt.Instance, new(), fixture.Attempt.Source.Blobs);

    private static Fixture Create(bool tasks, string? fault = null)
    {
        var attempt = AttemptTestFixture.Create(configurationRevision: 3);
        var originalTarget = Fingerprints(attempt.Configuration.TargetPath);
        var step = attempt.Source.Plan.Steps[0]; var blobs = attempt.Source.Blobs;
        var source = new ConfigurationRevision(new(Guid.Parse(step.BindingId!), "main"), 3, Guid.Parse(step.UserId!), Guid.Parse(step.ScriptId),
            new(1, new('c', 64), null, false), attempt.Configuration.Candidate.Snapshot);
        ImmutableArray<RunTaskRule> rules = [];
        if (tasks)
        {
            var body = blobs.PersistContent(Encoding.UTF8.GetBytes("{\"tasks\":{\"one\":false,\"foreign\":false},\"count\":17}"), default);
            source = source with { Tree = new(FileObjectKind.Directory,
                [new("", FileObjectKind.Directory, 0, null), new("tasks.json", FileObjectKind.File, body.SizeBytes, body.Sha256)]) };
            var selector = new ConfigurationSelector([new ConfigurationProperty("tasks")]);
            var authorized = fault == "undeclared-task" ? "foreign" : "one";
            rules = [new("tasks.json", ConfigurationSyntax.Json, new([new(selector, true)], [new(selector, true)], []),
                new(new ConfigurationMapTasks(selector), [authorized]), fault is "selected-ids" or "undeclared-task" ? [] : ["one"])];
        }
        var operation = attempt.Configuration.OperationId;
        var captured = new ConfigurationTaskTransformation(blobs).Capture(operation, source, rules);
        var state = new FrozenRunState(attempt.Instance.InstanceId, Guid.Parse(attempt.Plan.OperationId), operation, source, 1, true, captured);
        state = fault switch
        {
            "owner" => state with { OwnerOperationId = Guid.NewGuid() },
            "binding" => state with { Source = source with { Address = new(Guid.NewGuid(), source.Address.ResourceId) } },
            "user" => state with { Source = source with { UserId = Guid.NewGuid() } },
            "script" => state with { Source = source with { ScriptId = Guid.NewGuid() } },
            "binding-revision" => state with { BindingRevision = 2 },
            _ => state
        };
        BlobReference childReference; FrozenConfigurationExchange child;
        using (var frozenState = new RunStateCodec(attempt.Instance, new(), blobs).Freeze(state))
        {
            var selected = new ConfigurationTaskTransformation(blobs).Select(operation, state.Source, state.Tasks);
            child = attempt.Configuration with { RunState = frozenState.Reference, Candidate = Archive(selected) };
            using var prepared = new ConfigurationPlanCodec(attempt.Instance, new(), blobs).Freeze(child);
            childReference = prepared.Reference;
        }
        attempt = attempt with { Plan = attempt.Plan with { ConfigurationPlans = [new() { StepId = step.StepId,
            Plan = new() { RelativePath = childReference.RelativePath, SizeBytes = childReference.SizeBytes, Sha256 = childReference.Sha256 } }] } };
        return new(attempt, state, child, childReference, originalTarget);
    }

    private static ArchivedFileTree Archive(FileTreeSnapshot tree) => new(tree, [], tree.Entries.Where(item => item.Kind == FileObjectKind.File)
        .Select(item => new BlobReference($"blobs/content/{item.Sha256}.bin", item.SizeBytes, item.Sha256!)).Distinct().ToArray());

    private static string[] Fingerprints(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();

    private sealed record Fixture(AttemptTestFixture Attempt, FrozenRunState State, FrozenConfigurationExchange Child,
        BlobReference ChildReference, string[] OriginalTarget);
}
