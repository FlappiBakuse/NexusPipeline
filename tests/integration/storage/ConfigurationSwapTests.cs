using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed partial class ConfigurationSwapTests
{
    [Theory(DisplayName = "A Reopened Plan Drives Real Same Volume Swap And Restores Original Bytes And Physical Objects")]
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
        var plan = fixture.Plans.Read(fixture.PersistPlan());
        var prepared = fixture.Swap.Prepare(plan, fixture.Stopped, CancellationToken.None);
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
        using (fixture.Swap.HoldRestored(prepared, fixture.Stopped))
        {
            AssertOriginal(fixture);
            var original = plan.Before.Snapshot.Entries.FirstOrDefault(entry => entry.Kind == FileObjectKind.File);
            if (original is not null)
            {
                var path = Path.Combine(plan.TargetPath, original.RelativePath);
                Assert.Throws<IOException>(() => File.Open(path, FileMode.Open, FileAccess.Write, FileShare.Read));
            }
        }
        if (plan.Before.Snapshot.Shape != FileObjectKind.Missing)
        {
            File.WriteAllText(Path.Combine(prepared.WorkPath, "before"), "unexpected backup");
            Assert.Throws<IOException>(() => fixture.Swap.HoldRestored(prepared, fixture.Stopped));
        }
    }

    [Fact(DisplayName = "A Held Configuration Plan Serves Journal Reads Until Release And Still Checks The Revision Directory")]
    public void ReusesHeldPlan()
    {
        var fixture = Create("file", "file");
        var plan = fixture.Plan;
        var contracts = new ContractReader();
        var instance = new InstanceRoot(contracts).OpenExisting(fixture.RuntimeRoot);
        var store = new JournalStore(instance, contracts);
        var reference = fixture.PersistPlan();
        store.Create(JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "nexus.execution.journal", version = 1, instanceId = plan.InstanceId, journalId = plan.JournalId,
            operationId = plan.OperationId, operationKind = "configExchange", phase = "prepared", revision = 1,
            updatedAt = "2026-11-18T00:00:00Z", planPath = reference.RelativePath, planDigest = reference.Sha256,
            resourceKeys = new[] { "fixture:configuration" }, participants = Array.Empty<object>(), receipts = Array.Empty<object>()
        }));
        var path = Path.Combine(instance.RuntimeRoot, reference.RelativePath);
        using (var opened = fixture.Plans.OpenStructure(reference))
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            locked.Lock(0, locked.Length);
            Assert.Throws<IOException>(() => store.Read(plan.JournalId));
            var physical = store.Read(plan.JournalId, opened);
            Assert.Equal(plan.JournalId, opened.ReadContents(instance, physical).JournalId);
            var unknown = Path.Combine(instance.RuntimeRoot, "journals", plan.JournalId.ToString("D"), "unknown");
            File.WriteAllText(unknown, "unexpected journal entry");
            Assert.Throws<IOException>(() => store.Read(plan.JournalId, opened));
            File.Delete(unknown);
        }
        if (OperatingSystem.IsWindows())
        {
            using var paths = DirectoryAttributeScope.Hold(instance);
            using var history = store.ProtectReadHistory(plan.JournalId, requireFreshInventory: true);
            Assert.Equal(plan.JournalId, store.Read(plan.JournalId).JournalId);
            var unknown = Path.Combine(instance.RuntimeRoot, "journals", plan.JournalId.ToString("D"), "unknown");
            File.WriteAllText(unknown, "unexpected journal entry");
            Assert.Throws<IOException>(() => store.Read(plan.JournalId));
            File.Delete(unknown);
        }
        File.WriteAllText(path, "changed after release");
        Assert.Throws<IOException>(() => fixture.Plans.OpenStructure(reference));
    }

    [Theory(DisplayName = "A New Effect Adapter Can Restore From The Persisted Plan At Every Completed Physical Boundary")]
    [InlineData("before-preserve")]
    [InlineData("after-preserve")]
    [InlineData("after-install")]
    [InlineData("after-retire")]
    public void ResumesCompletedEffects(string boundary)
    {
        var fixture = Create("directory", "file");
        var reference = fixture.PersistPlan();
        var prepared = fixture.Swap.Prepare(fixture.Plan, fixture.Stopped, CancellationToken.None);
        if (boundary != "before-preserve") fixture.Swap.PreserveOriginal(prepared, fixture.Stopped);
        if (boundary is "after-install" or "after-retire") fixture.Swap.InstallCandidate(prepared, fixture.Stopped);
        if (boundary == "after-retire") fixture.Swap.RetireCandidate(prepared, fixture.Stopped);
        var restarted = new ConfigurationSwap(fixture.Scanner, fixture.Archive);
        var codec = new ConfigurationWorkspaceCodec(new ContractReader());
        var identity = codec.Encode(prepared, reference.Sha256);
        prepared = codec.Decode(identity, fixture.Plans.Read(reference), reference.Sha256);
        restarted.RetireCandidate(prepared, fixture.Stopped);
        restarted.RestoreOriginal(prepared, fixture.Stopped);
        restarted.VerifyRestored(prepared, fixture.Stopped);
        AssertOriginal(fixture);
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
    [InlineData("child-identity")]
    [InlineData("unknown-work-entry")]
    [InlineData("wrong-parent-identity")]
    public void PreservesUnknownChanges(string fault)
    {
        var fixture = Create(fault == "child-identity" ? "directory" : "file", "directory");
        var targetPath = fixture.Plan.TargetPath;
        var checkedPath = fault == "child-identity" ? Path.Combine(targetPath, "nested", "配置") : targetPath;
        var prepared = fixture.Swap.Prepare(fixture.Plan, fixture.Stopped, CancellationToken.None);
        if (fault == "target-bytes") File.WriteAllText(targetPath, "foreign bytes");
        if (fault is "target-identity" or "child-identity")
        {
            var original = Path.Combine(fixture.Root, "preserved-original");
            File.Move(checkedPath, original);
            File.Copy(original, checkedPath);
        }
        if (fault == "unknown-work-entry") File.WriteAllText(Path.Combine(prepared.WorkPath, "unknown"), "preserve");
        if (fault == "wrong-parent-identity") prepared = prepared with { Plan = prepared.Plan with { ParentIdentity = PhysicalObjectIdentity.Read(fixture.RuntimeRoot) } };
        var target = fixture.Scanner.Capture(targetPath, CancellationToken.None);
        var identity = PhysicalObjectIdentity.Read(checkedPath);
        Assert.Throws<IOException>(() => fixture.Swap.PreserveOriginal(prepared, fixture.Stopped));
        Assert.Equal(target.Entries, fixture.Scanner.Capture(targetPath, CancellationToken.None).Entries);
        Assert.Equal(identity, PhysicalObjectIdentity.Read(checkedPath));
        Assert.False(Path.Exists(Path.Combine(prepared.WorkPath, "before")));
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

    private static void AssertOriginal(Fixture fixture)
    {
        var restored = fixture.Scanner.Capture(fixture.Plan.TargetPath, CancellationToken.None);
        Assert.Equal(fixture.Plan.Before.Snapshot.Shape, restored.Shape);
        Assert.Equal(fixture.Plan.Before.Snapshot.Entries, restored.Entries);
        Assert.Equal(fixture.Plan.Before.OriginalObjects, PhysicalTreeIdentity.Capture(fixture.Plan.TargetPath, restored));
    }

    private static Fixture Create(string before, string candidate, Action<RuntimeInstance>? initialize = null)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new InvalidOperationException(), Guid.NewGuid().ToString("N"));
        var contracts = new ContractReader();
        var instance = new InstanceRoot(contracts).OpenOrCreate(Path.Combine(root, "runtime"));
        initialize?.Invoke(instance);
        var parent = Path.Combine(root, "configuration");
        Directory.CreateDirectory(parent);
        var target = Path.Combine(parent, "target");
        var input = Path.Combine(root, "input");
        Prepare(target, before, [0, 1, 255]);
        Prepare(input, candidate, [255, 1, 0]);
        var scanner = new FileTreeScanner(SnapshotBudget.Default);
        var blobs = new ImmutableBlobStore(instance, contracts);
        var archive = new FileTreeArchive(scanner, blobs);
        var plan = new FrozenConfigurationExchange(instance.InstanceId, Guid.NewGuid(), Guid.NewGuid(), JournalKind.ConfigExchange,
            target, PhysicalObjectIdentity.Read(parent), archive.Capture(target, CancellationToken.None), archive.Capture(input, CancellationToken.None));
        return new(root, instance.RuntimeRoot, plan, new(instance.InstanceId, plan.JournalId, true, WriterState.Stopped),
            scanner, archive, new(scanner, archive), new(instance, contracts, blobs));
    }

    private static void Prepare(string path, string shape, byte[] bytes)
    {
        if (shape == "file") File.WriteAllBytes(path, bytes);
        if (shape != "directory") return;
        Directory.CreateDirectory(Path.Combine(path, "nested", "empty"));
        File.WriteAllBytes(Path.Combine(path, "nested", "配置"), bytes);
    }

    private sealed record Fixture(string Root, string RuntimeRoot, FrozenConfigurationExchange Plan, ConfigurationWriterObservation Stopped,
        FileTreeScanner Scanner, FileTreeArchive Archive, ConfigurationSwap Swap, ConfigurationPlanCodec Plans)
    {
        internal BlobReference PersistPlan()
        {
            using var frozen = Plans.Freeze(Plan);
            return frozen.Reference;
        }
    }
}
