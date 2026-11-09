using System.Text.Json.Nodes;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class ConfigurationPlanTests
{
    [Theory(DisplayName = "Invalid Plan Is Rejected Without Changing Its Bytes Or The Managed Target")]
    [InlineData("unknown-version", "unsupported_or_invalid_contract")]
    [InlineData("executable-action", "unsupported_or_invalid_contract")]
    [InlineData("path-escape", "unsupported_snapshot_relative_path")]
    [InlineData("missing-physical-identity", "configuration_plan_original_identity_incomplete")]
    [InlineData("incomplete-tree", "file_tree_archive_shape_invalid")]
    public void PreservesInvalidPlan(string fault, string reason)
    {
        var fixture = Create("directory", "file");
        using var prepared = fixture.Codec.Freeze(fixture.Plan);
        var valid = prepared.Reference;
        var path = Path.Combine(fixture.RuntimeRoot, valid.RelativePath);
        var document = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        switch (fault)
        {
            case "unknown-version": document["version"] = 17; break;
            case "executable-action": document["command"] = "arbitrary.exe"; break;
            case "path-escape":
                var file = document["before"]!["entries"]!.AsArray().Single(item => item!["kind"]!.GetValue<string>() == "file")!;
                var identity = document["originalObjects"]!.AsArray().Single(item =>
                    item!["relativePath"]!.GetValue<string>() == file["relativePath"]!.GetValue<string>())!;
                file["relativePath"] = @"..\outside";
                identity["relativePath"] = @"..\outside";
                break;
            case "missing-physical-identity": document["originalObjects"] = new JsonArray(); break;
            case "incomplete-tree": document["candidate"]!["entries"] = new JsonArray(); break;
        }
        var invalid = fixture.Blobs.PersistPlan(JsonSerializer.SerializeToUtf8Bytes(document));
        var invalidPath = Path.Combine(fixture.RuntimeRoot, invalid.RelativePath);
        var before = File.ReadAllBytes(invalidPath);
        var targetBefore = fixture.Scanner.Capture(fixture.Plan.TargetPath, CancellationToken.None);
        Assert.Equal(reason, Record.Exception(() => fixture.Codec.Read(invalid))?.Message);
        Assert.Equal(before, File.ReadAllBytes(invalidPath));
        Assert.Equal(targetBefore.Entries, fixture.Scanner.Capture(fixture.Plan.TargetPath, CancellationToken.None).Entries);
    }

    [Theory(DisplayName = "Plan Cannot Own The Runtime Or Claim Another Instance Or Operation Kind")]
    [InlineData("runtime-child")]
    [InlineData("runtime-parent")]
    [InlineData("foreign-instance")]
    [InlineData("wrong-kind")]
    public void RejectsUnboundPlans(string fault)
    {
        var fixture = Create("file", "file");
        var invalid = fault switch
        {
            "runtime-child" => fixture.Plan with { TargetPath = Path.Combine(fixture.RuntimeRoot, "state") },
            "runtime-parent" => fixture.Plan with { TargetPath = Path.GetDirectoryName(fixture.RuntimeRoot)! },
            "foreign-instance" => fixture.Plan with { InstanceId = Guid.NewGuid() },
            _ => fixture.Plan with { Kind = JournalKind.UpdateApply }
        };
        Assert.Throws<IOException>(() => fixture.Codec.Freeze(invalid));
        Assert.False(Directory.Exists(Path.Combine(fixture.RuntimeRoot, "blobs", "plans")));
    }

    [Fact(DisplayName = "A Prepared Plan Retains Verified Contents And Its Own Bytes Until Acceptance Finishes")]
    public void HoldsPreparedContent()
    {
        var fixture = Create("directory", "file");
        using var prepared = fixture.Codec.Freeze(fixture.Plan);
        var paths = prepared.Plan.Before.Contents.Concat(prepared.Plan.Candidate.Contents).Append(prepared.Reference)
            .Select(reference => Path.Combine(fixture.RuntimeRoot, reference.RelativePath)).Distinct().ToArray();
        foreach (var path in paths)
        {
            Assert.Throws<IOException>(() => File.Delete(path));
            Assert.Throws<IOException>(() => { using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite); });
        }
        prepared.Dispose();
        foreach (var path in paths)
            using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    }

    [Fact(DisplayName = "Caller Array Mutation Cannot Change An Already Prepared Configuration Plan")]
    public void OwnsCallerArrays()
    {
        var fixture = Create("file", "file");
        var entries = fixture.Plan.Before.Snapshot.Entries.ToArray();
        var identities = fixture.Plan.Before.OriginalObjects.ToArray();
        var contents = fixture.Plan.Before.Contents.ToArray();
        var mutable = fixture.Plan with { Before = new(fixture.Plan.Before.Snapshot with { Entries = entries }, identities, contents) };
        using var prepared = fixture.Codec.Freeze(mutable);
        entries[0] = entries[0] with { Sha256 = new string('a', 64) };
        identities[0] = identities[0] with { Identity = PhysicalObjectIdentity.Read(fixture.Root) };
        contents[0] = contents[0] with { SizeBytes = 0 };
        Assert.Equal(fixture.Plan.Before.Snapshot.Entries, prepared.Plan.Before.Snapshot.Entries);
        Assert.Equal(fixture.Plan.Before.OriginalObjects, prepared.Plan.Before.OriginalObjects);
        Assert.Equal(fixture.Plan.Before.Contents, prepared.Plan.Before.Contents);
    }

    private static Fixture Create(string before, string candidate)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new InvalidOperationException(), Guid.NewGuid().ToString("N"));
        var contracts = new ContractReader();
        var instance = new InstanceRoot(contracts).OpenOrCreate(Path.Combine(root, "runtime"));
        var target = Path.Combine(root, "target");
        var candidatePath = Path.Combine(root, "input-candidate");
        Prepare(target, before);
        Prepare(candidatePath, candidate);
        var blobs = new ImmutableBlobStore(instance, contracts);
        var scanner = new FileTreeScanner(SnapshotBudget.Default);
        var archive = new FileTreeArchive(scanner, blobs);
        var plan = new FrozenConfigurationExchange(instance.InstanceId, Guid.NewGuid(), Guid.NewGuid(), JournalKind.ConfigExchange,
            target, PhysicalObjectIdentity.Read(root), archive.Capture(target, CancellationToken.None), archive.Capture(candidatePath, CancellationToken.None));
        return new(root, instance.RuntimeRoot, plan, new(instance, contracts, blobs), blobs, scanner);
    }

    private static void Prepare(string path, string shape)
    {
        if (shape == "file") File.WriteAllBytes(path, [0, 1, 255]);
        if (shape != "directory") return;
        Directory.CreateDirectory(Path.Combine(path, "配置"));
        File.WriteAllBytes(Path.Combine(path, "配置", "data.bin"), [0, 1, 255]);
    }

    private sealed record Fixture(string Root, string RuntimeRoot, FrozenConfigurationExchange Plan,
        ConfigurationPlanCodec Codec, ImmutableBlobStore Blobs, FileTreeScanner Scanner);
}
