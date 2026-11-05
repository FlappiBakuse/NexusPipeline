using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class AssetPlanTests
{
    [Fact(DisplayName = "Verified Attachment Sources Stay Read Only Until Their Preparation Lease Is Released")]
    public void HoldsVerifiedSources()
    {
        var fixture = Create();
        using var prepared = fixture.Codec.Freeze(fixture.Preview, fixture.Input, Guid.NewGuid());
        var asset = Assert.Single(prepared.Plan.ReadWire().Assets);
        var sources = new AssetPreparationSources(prepared.Plan, new Dictionary<Guid, string> { [Guid.Parse(asset.AssetId)] = fixture.SourceAsset });
        try
        {
            Assert.Throws<IOException>(() => { using var write = new FileStream(fixture.SourceAsset, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); });
            Assert.Throws<IOException>(() => File.Move(fixture.SourceAsset, fixture.SourceAsset + ".moved"));
        }
        finally { sources.Dispose(); }
        using var writable = new FileStream(fixture.SourceAsset, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        Assert.Equal(3, writable.Length);
    }

    [Theory(DisplayName = "Unverified Attachment Sources Cannot Write Content Or Retain A Failed Source Lease")]
    [InlineData("bytes")]
    [InlineData("size")]
    [InlineData("missing-selection")]
    [InlineData("unknown-selection")]
    public void RejectsUnverifiedSources(string fault)
    {
        var fixture = Create();
        using var prepared = fixture.Codec.Freeze(fixture.Preview, fixture.Input, Guid.NewGuid());
        var asset = Assert.Single(prepared.Plan.ReadWire().Assets);
        var selected = new Dictionary<Guid, string> { [Guid.Parse(asset.AssetId)] = fixture.SourceAsset };
        if (fault == "bytes") File.WriteAllBytes(fixture.SourceAsset, [5, 7, 13]);
        if (fault == "size") File.WriteAllBytes(fixture.SourceAsset, [5, 7]);
        if (fault == "missing-selection") selected.Clear();
        if (fault == "unknown-selection") selected.Add(Guid.NewGuid(), fixture.SourceAsset);
        var before = Fingerprints(fixture.Instance.RuntimeRoot);
        Assert.Throws<IOException>(() => new AssetPreparationSources(prepared.Plan, selected));
        Assert.Equal(before, Fingerprints(fixture.Instance.RuntimeRoot));
        using var writable = new FileStream(fixture.SourceAsset, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
    }

    [Fact(DisplayName = "An Existing Workspace Cannot Be Adopted By Freezing Another Asset Preparation")]
    public void RejectsForeignWorkspaces()
    {
        var fixture = Create();
        var journalId = Guid.NewGuid();
        var path = Path.Combine(fixture.Instance.RuntimeRoot, "asset-preparations", journalId.ToString("D"));
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "unowned.txt"), "preserve");
        var before = Fingerprints(fixture.Instance.RuntimeRoot);
        Assert.Throws<IOException>(() => fixture.Codec.Freeze(fixture.Preview, fixture.Input, journalId));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(path, "unowned.txt")));
        Assert.Equal(before.Where(value => value.Contains("unowned.txt", StringComparison.Ordinal)),
            Fingerprints(fixture.Instance.RuntimeRoot).Where(value => value.Contains("unowned.txt", StringComparison.Ordinal)));
    }

    [Fact(DisplayName = "Freezing An Asset Intent Owns Its Input Bytes And Does Not Create Its Unprepared Content")]
    public void OwnsFrozenInputs()
    {
        var fixture = Create();
        var originalInput = fixture.Input.ToArray();
        using var prepared = fixture.Codec.Freeze(fixture.Preview, fixture.Input, Guid.NewGuid());
        fixture.Input[0] = 0;
        var bytes = prepared.Plan.Export();
        bytes[0] = 0;
        var frozen = prepared.Plan.ReadWire();
        Assert.Equal(fixture.Instance.InstanceId.ToString("D"), frozen.InstanceId);
        Assert.Equal(fixture.Preview.OperationId.ToString("D"), frozen.OperationId);
        var asset = Assert.Single(frozen.Assets);
        Assert.False(asset.WasPresent);
        Assert.Null(asset.ExistingIdentity);
        Assert.False(File.Exists(Path.Combine(fixture.Instance.RuntimeRoot, asset.Content.RelativePath)));
        Assert.Equal(originalInput, File.ReadAllBytes(Path.Combine(fixture.Instance.RuntimeRoot, frozen.Input.RelativePath)));
        Assert.Equal(fixture.Preview.InputDigest, frozen.Input.Sha256);
        Assert.Equal(prepared.Plan.Export(), fixture.Codec.Read(prepared.Reference).Export());
    }

    [Fact(DisplayName = "An Existing Content Object Keeps Its Exact Identity And Cannot Be Written While Its Prepared Intent Is Leased")]
    public void PinsExistingContent()
    {
        var fixture = Create();
        var tree = new FileTreeScanner(SnapshotBudget.Default).Capture(fixture.SourceAsset, default);
        var reference = fixture.Blobs.PersistContent(fixture.SourceAsset, tree.Entries.Single(), default);
        var physical = Path.Combine(fixture.Instance.RuntimeRoot, reference.RelativePath);
        var identity = PhysicalObjectIdentity.Read(physical);
        var prepared = fixture.Codec.Freeze(fixture.Preview, fixture.Input, Guid.NewGuid());
        try
        {
            var item = Assert.Single(prepared.Plan.ReadWire().Assets);
            Assert.True(item.WasPresent);
            Assert.Equal(identity.FileId, item.ExistingIdentity!.FileId);
            Assert.Throws<IOException>(() => { using var write = new FileStream(physical, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); });
        }
        finally { prepared.Dispose(); }
        using var writable = new FileStream(physical, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        Assert.Equal(3, writable.Length);
        Assert.Throws<ObjectDisposedException>(() => prepared.Reference);
    }

    [Theory(DisplayName = "A Valid Digest Cannot Authorize An Asset Intent With Unbound Input Or Output Scope")]
    [InlineData("input-path")]
    [InlineData("asset-path")]
    [InlineData("asset-size")]
    [InlineData("asset-id")]
    public void RejectsUnboundScopes(string fault)
    {
        var fixture = Create();
        using var prepared = fixture.Codec.Freeze(fixture.Preview, fixture.Input, Guid.NewGuid());
        var plan = prepared.Plan.ReadWire();
        plan = fault switch
        {
            "input-path" => plan with { Input = plan.Input with { RelativePath = $"blobs/content/{plan.Input.Sha256}.bin" } },
            "asset-path" => plan with { Assets = [plan.Assets[0] with { Content = plan.Assets[0].Content with { RelativePath = "blobs/content/other.bin" } }] },
            "asset-size" => plan with { Assets = [plan.Assets[0] with { Content = plan.Assets[0].Content with { SizeBytes = 2 } }] },
            _ => plan with { Assets = [plan.Assets[0] with { AssetId = Guid.NewGuid().ToString("D") }] }
        };
        var malformed = fixture.Blobs.PersistPlan(BoundedJson.Serialize(plan));
        var before = Fingerprints(fixture.Instance.RuntimeRoot);
        Assert.Throws<IOException>(() => fixture.Codec.Read(malformed));
        Assert.Equal(before, Fingerprints(fixture.Instance.RuntimeRoot));
    }

    [Fact(DisplayName = "Changing The Previewed Input Is Rejected Before Publishing Any Preparation Plan")]
    public void RechecksPreviewedInputs()
    {
        var fixture = Create();
        var before = Fingerprints(fixture.Instance.RuntimeRoot);
        var changed = fixture.Input.Concat(new byte[] { 32 }).ToArray();
        Assert.Throws<IOException>(() => fixture.Codec.Freeze(fixture.Preview, changed, Guid.NewGuid()));
        Assert.Equal(before, Fingerprints(fixture.Instance.RuntimeRoot));
    }

    [Fact(DisplayName = "An Unknown Referenced Definition Format Is Rejected Without Changing Its Bytes Or The Asset Site")]
    public void PreservesUnknownDefinitions()
    {
        var fixture = Create();
        using var prepared = fixture.Codec.Freeze(fixture.Preview, fixture.Input, Guid.NewGuid());
        var input = System.Text.Json.Nodes.JsonNode.Parse(fixture.Input)!;
        input["version"] = 77;
        var unknownInput = fixture.Blobs.PersistPlan(JsonSerializer.SerializeToUtf8Bytes(input));
        var wire = prepared.Plan.ReadWire();
        wire = wire with { Input = new() { RelativePath = unknownInput.RelativePath, Sha256 = unknownInput.Sha256, SizeBytes = unknownInput.SizeBytes } };
        var unknownPlan = fixture.Blobs.PersistPlan(BoundedJson.Serialize(wire));
        var before = Fingerprints(fixture.Instance.RuntimeRoot);
        Assert.Throws<JsonException>(() => fixture.Codec.Read(unknownPlan));
        Assert.Equal(before, Fingerprints(fixture.Instance.RuntimeRoot));
    }

    private static Fixture Create()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "asset-preparation-" + Guid.NewGuid().ToString("N"));
        var instance = new InstanceRoot(new()).OpenOrCreate(Path.Combine(root, "runtime"));
        var database = StateDatabase.CreateFresh(instance);
        var definitions = AutomationEdits.Apply(AutomationCatalog.Empty, AutomationDefinitionSeed.Create(root));
        var asset = Path.Combine(root, "source.asset");
        File.WriteAllBytes(asset, [5, 7, 11]);
        var declared = new AutomationImportAttachment(Guid.NewGuid(), "attachments/data.bin", 3,
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(asset))));
        var input = new AutomationBundleCodec(new()).Encode(definitions, DateTimeOffset.UtcNow, selectedAttachments: [declared]);
        var selected = definitions.Roots.ToDictionary(value => value.Id, value => value.Path);
        var preview = new AutomationImportPreviewer(instance, new(), database).Preview(Guid.NewGuid(), input, selected, 0);
        var blobs = new ImmutableBlobStore(instance, new());
        return new(root, instance, asset, input, preview, blobs, new(instance, new(), blobs));
    }

    private static string[] Fingerprints(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
    private sealed record Fixture(string Root, RuntimeInstance Instance, string SourceAsset, byte[] Input,
        AutomationImportPreview Preview, ImmutableBlobStore Blobs, AssetPlanCodec Codec);
}
