using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class ConfigurationSwapTests
{
    [Theory(DisplayName = "Real Same Volume Swap And Restoration Preserve Original Bytes And Physical Objects")]
    [InlineData("missing", "missing")]
    [InlineData("missing", "file")]
    [InlineData("missing", "directory")]
    [InlineData("file", "missing")]
    [InlineData("file", "file")]
    [InlineData("file", "directory")]
    [InlineData("directory", "missing")]
    [InlineData("directory", "file")]
    [InlineData("directory", "directory")]
    public void RestoresPhysicalObjects(string before, string candidate)
    {
        var fixture = Create(before, candidate);
        var prepared = fixture.Swap.Prepare(fixture.Plan, fixture.Stopped, CancellationToken.None);
        fixture.Swap.PreserveOriginal(prepared, fixture.Stopped);
        fixture.Swap.PreserveOriginal(prepared, fixture.Stopped);
        Assert.Equal(FileObjectKind.Missing, fixture.Scanner.Capture(fixture.Plan.TargetPath, CancellationToken.None).Shape);
        fixture.Swap.InstallCandidate(prepared, fixture.Stopped);
        fixture.Swap.InstallCandidate(prepared, fixture.Stopped);
        var installed = fixture.Scanner.Capture(fixture.Plan.TargetPath, CancellationToken.None);
        Assert.Equal(fixture.Plan.Candidate.Snapshot.Shape, installed.Shape);
        Assert.Equal(fixture.Plan.Candidate.Snapshot.Entries, installed.Entries);
        fixture.Swap.RetireCandidate(prepared, fixture.Stopped);
        fixture.Swap.RetireCandidate(prepared, fixture.Stopped);
        fixture.Swap.RestoreOriginal(prepared, fixture.Stopped);
        fixture.Swap.RestoreOriginal(prepared, fixture.Stopped);
        fixture.Swap.VerifyRestored(prepared, fixture.Stopped);
        var restored = fixture.Scanner.Capture(fixture.Plan.TargetPath, CancellationToken.None);
        Assert.Equal(fixture.Plan.Before.Snapshot.Shape, restored.Shape);
        Assert.Equal(fixture.Plan.Before.Snapshot.Entries, restored.Entries);
        Assert.Equal(fixture.Plan.Before.OriginalObjects, PhysicalTreeIdentity.Capture(fixture.Plan.TargetPath, restored));
    }

    [Theory(DisplayName = "A New Effect Adapter Can Restore At Every Completed Physical Boundary")]
    [InlineData("before-preserve")]
    [InlineData("after-preserve")]
    [InlineData("after-install")]
    [InlineData("after-retire")]
    public void ResumesCompletedEffects(string boundary)
    {
        var fixture = Create("directory", "file");
        var prepared = fixture.Swap.Prepare(fixture.Plan, fixture.Stopped, CancellationToken.None);
        if (boundary != "before-preserve") fixture.Swap.PreserveOriginal(prepared, fixture.Stopped);
        if (boundary is "after-install" or "after-retire") fixture.Swap.InstallCandidate(prepared, fixture.Stopped);
        if (boundary == "after-retire") fixture.Swap.RetireCandidate(prepared, fixture.Stopped);
        var restarted = new ConfigurationSwap(fixture.Scanner, fixture.Archive);
        var codec = new ConfigurationWorkspaceCodec(new ContractReader());
        var identity = codec.Encode(prepared, new string('a', 64));
        prepared = new ConfigurationWorkspaceCodec(new ContractReader()).Decode(identity, fixture.Plan, new string('a', 64));
        restarted.RetireCandidate(prepared, fixture.Stopped);
        restarted.RestoreOriginal(prepared, fixture.Stopped);
        restarted.VerifyRestored(prepared, fixture.Stopped);
        Assert.Equal(fixture.Plan.Before.Snapshot.Entries, fixture.Scanner.Capture(fixture.Plan.TargetPath, CancellationToken.None).Entries);
    }

    [Theory(DisplayName = "Missing Or Unbound Writer Stop Evidence Prevents Preparation And Every Physical Mutation")]
    [InlineData("running")]
    [InlineData("unknown")]
    [InlineData("incomplete")]
    [InlineData("foreign-instance")]
    [InlineData("foreign-journal")]
    public void RequiresStoppedWriters(string fault)
    {
        var fixture = Create("file", "directory");
        var writers = fault switch
        {
            "running" => fixture.Stopped with { State = WriterState.Running },
            "unknown" => fixture.Stopped with { State = WriterState.Unknown },
            "incomplete" => fixture.Stopped with { IsComplete = false },
            "foreign-instance" => fixture.Stopped with { InstanceId = Guid.NewGuid() },
            _ => fixture.Stopped with { JournalId = Guid.NewGuid() }
        };
        Assert.Throws<IOException>(() => fixture.Swap.Prepare(fixture.Plan, writers, CancellationToken.None));
        var prepared = fixture.Swap.Prepare(fixture.Plan, fixture.Stopped, CancellationToken.None);
        Assert.Throws<IOException>(() => fixture.Swap.PreserveOriginal(prepared, writers));
        Assert.Throws<IOException>(() => fixture.Swap.InstallCandidate(prepared, writers));
        Assert.Throws<IOException>(() => fixture.Swap.RetireCandidate(prepared, writers));
        Assert.Throws<IOException>(() => fixture.Swap.RestoreOriginal(prepared, writers));
        Assert.Equal(fixture.Plan.Before.Snapshot.Entries, fixture.Scanner.Capture(fixture.Plan.TargetPath, CancellationToken.None).Entries);
    }

    [Theory(DisplayName = "Unexpected Bytes Or Physical Identity Changes Are Preserved Instead Of Moved Or Overwritten")]
    [InlineData("target-bytes")]
    [InlineData("target-identity")]
    [InlineData("unknown-work-entry")]
    [InlineData("wrong-parent-identity")]
    public void PreservesUnknownChanges(string fault)
    {
        var fixture = Create("file", "directory");
        var prepared = fixture.Swap.Prepare(fixture.Plan, fixture.Stopped, CancellationToken.None);
        if (fault == "target-bytes") File.WriteAllText(fixture.Plan.TargetPath, "foreign bytes");
        if (fault == "target-identity")
        {
            var bytes = File.ReadAllBytes(fixture.Plan.TargetPath);
            File.Move(fixture.Plan.TargetPath, Path.Combine(fixture.Root, "preserved-original"));
            File.WriteAllBytes(fixture.Plan.TargetPath, bytes);
        }
        if (fault == "unknown-work-entry") File.WriteAllText(Path.Combine(prepared.WorkPath, "unknown"), "preserve");
        if (fault == "wrong-parent-identity") prepared = prepared with { Plan = prepared.Plan with { ParentIdentity = PhysicalObjectIdentity.Read(fixture.RuntimeRoot) } };
        var target = fixture.Scanner.Capture(fixture.Plan.TargetPath, CancellationToken.None);
        Assert.Throws<IOException>(() => fixture.Swap.PreserveOriginal(prepared, fixture.Stopped));
        Assert.Equal(target.Entries, fixture.Scanner.Capture(fixture.Plan.TargetPath, CancellationToken.None).Entries);
        Assert.False(File.Exists(Path.Combine(prepared.WorkPath, "before")));
        Assert.False(Directory.Exists(Path.Combine(prepared.WorkPath, "before")));
        if (fault == "unknown-work-entry") Assert.Equal("preserve", File.ReadAllText(Path.Combine(prepared.WorkPath, "unknown")));
    }

    [Fact(DisplayName = "An Actual Open Writer Prevents Preparation Before Any Adjacent Work Is Created")]
    public void RejectsOpenWriters()
    {
        var fixture = Create("file", "file");
        using var writer = new FileStream(fixture.Plan.TargetPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        Assert.Throws<IOException>(() => fixture.Swap.Prepare(fixture.Plan, fixture.Stopped, CancellationToken.None));
        Assert.Single(Directory.GetFileSystemEntries(Path.GetDirectoryName(fixture.Plan.TargetPath)!));
    }

    [Fact(DisplayName = "Unknown Changes After Installation Cannot Be Discarded During Restoration")]
    public void PreservesUnknownOutput()
    {
        var fixture = Create("directory", "directory");
        var prepared = fixture.Swap.Prepare(fixture.Plan, fixture.Stopped, CancellationToken.None);
        fixture.Swap.PreserveOriginal(prepared, fixture.Stopped);
        fixture.Swap.InstallCandidate(prepared, fixture.Stopped);
        var unknown = Path.Combine(fixture.Plan.TargetPath, "unknown-user-data");
        File.WriteAllText(unknown, "preserve");
        Assert.Throws<IOException>(() => fixture.Swap.RetireCandidate(prepared, fixture.Stopped));
        Assert.Throws<IOException>(() => fixture.Swap.RestoreOriginal(prepared, fixture.Stopped));
        Assert.Equal("preserve", File.ReadAllText(unknown));
        Assert.Equal(fixture.Plan.Before.Snapshot.Entries, fixture.Scanner.Capture(Path.Combine(prepared.WorkPath, "before"), CancellationToken.None).Entries);
    }

    [Theory(DisplayName = "A Workspace Receipt Cannot Authorize An Unrelated Or Incomplete Physical Site")]
    [InlineData("instance")]
    [InlineData("journal")]
    [InlineData("plan")]
    [InlineData("workspace")]
    [InlineData("candidate-inventory")]
    [InlineData("candidate-aliases-original")]
    public void RejectsUnboundReceipts(string fault)
    {
        var fixture = Create("file", "directory");
        var prepared = fixture.Swap.Prepare(fixture.Plan, fixture.Stopped, CancellationToken.None);
        var codec = new ConfigurationWorkspaceCodec(new ContractReader());
        var document = JsonNode.Parse(codec.Encode(prepared, new string('a', 64)).GetRawText())!.AsObject();
        if (fault == "instance") document["instanceId"] = Guid.NewGuid().ToString("D");
        if (fault == "journal") document["journalId"] = Guid.NewGuid().ToString("D");
        if (fault == "plan") document["planDigest"] = new string('b', 64);
        if (fault == "workspace") document["workPath"] = fixture.Root;
        if (fault == "candidate-inventory") document["candidateObjects"] = new JsonArray();
        if (fault == "candidate-aliases-original") document["candidateObjects"]![0]!["identity"] =
            JsonSerializer.SerializeToNode(fixture.Plan.Before.OriginalIdentity, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var input = JsonSerializer.SerializeToElement(document);
        Assert.Throws<IOException>(() => codec.Decode(input, fixture.Plan, new string('a', 64)));
        Assert.Equal(fixture.Plan.Before.Snapshot.Entries, fixture.Scanner.Capture(fixture.Plan.TargetPath, CancellationToken.None).Entries);
    }

    private static Fixture Create(string before, string candidate)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new InvalidOperationException(), Guid.NewGuid().ToString("N"));
        var contracts = new ContractReader();
        var instance = new InstanceRoot(contracts).OpenOrCreate(Path.Combine(root, "runtime"));
        var parent = Path.Combine(root, "configuration");
        Directory.CreateDirectory(parent);
        var target = Path.Combine(parent, "target");
        var input = Path.Combine(root, "input");
        Prepare(target, before, "original");
        Prepare(input, candidate, "candidate");
        var scanner = new FileTreeScanner(SnapshotBudget.Default);
        var archive = new FileTreeArchive(scanner, new ImmutableBlobStore(instance, contracts));
        var plan = new FrozenConfigurationExchange(instance.InstanceId, Guid.NewGuid(), Guid.NewGuid(), JournalKind.ConfigExchange,
            target, PhysicalObjectIdentity.Read(parent), archive.Capture(target, CancellationToken.None), archive.Capture(input, CancellationToken.None));
        return new(root, instance.RuntimeRoot, plan, new(instance.InstanceId, plan.JournalId, true, WriterState.Stopped), scanner, archive, new(scanner, archive));
    }

    private static void Prepare(string path, string shape, string bytes)
    {
        if (shape == "file") File.WriteAllText(path, bytes);
        if (shape != "directory") return;
        Directory.CreateDirectory(Path.Combine(path, "nested", "empty"));
        File.WriteAllText(Path.Combine(path, "nested", "config"), bytes);
    }

    private sealed record Fixture(string Root, string RuntimeRoot, FrozenConfigurationExchange Plan, ConfigurationWriterObservation Stopped,
        FileTreeScanner Scanner, FileTreeArchive Archive, ConfigurationSwap Swap);
}
