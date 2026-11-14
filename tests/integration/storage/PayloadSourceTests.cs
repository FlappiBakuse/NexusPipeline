using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class PayloadSourceTests
{
    [Fact(DisplayName = "The Frozen Payload Source Binds The Actual Accepted Catalog Original Package And Saved Disabled Preference")]
    public void BindsAcceptedSource()
    {
        using var fixture = new PayloadSourceFixture();
        fixture.RequireContents(fixture.Source);
        var codec = new PayloadPlanCodec(fixture.Package.Catalog.Instance, fixture.Package.Catalog.Contracts,
            fixture.Package.Blobs, fixture.Roots);
        using var frozen = codec.Freeze(fixture.Plan);
        var loaded = codec.Read(frozen.Reference);
        Assert.Equal(fixture.SourceReference, loaded.ExtensionSource);
        var source = fixture.SourceCodec.ReadMetadata(loaded.ExtensionSource!);
        Assert.Equal(fixture.Source.SettingsRevision, source.SettingsRevision);
        Assert.False(source.DesiredEnabled);
        fixture.SourceCodec.RequireFrozenBinding(source, loaded);
        Assert.Contains(fixture.Source.Package!.Archive,
            NexusPipeline.Platform.Storage.Extensions.PayloadSourceCodec.References(source));
        Assert.False(Directory.Exists(fixture.Plan.TargetPath));
        using var connection = DatabaseConnections.Open(fixture.Package.Catalog.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Extensions);
        using var transaction = connection.BeginTransaction(deferred: true);
        Assert.Null(ExtensionInstalledRows.ReadCurrentMetadata(connection, transaction, "fixture-plugin"));
    }

    [Fact(DisplayName = "Metadata Reading Does Not Open The Original Archive Content")]
    public void ReadsMetadataOnly()
    {
        using var fixture = new PayloadSourceFixture();
        var path = Path.Combine(fixture.Package.Catalog.Instance.RuntimeRoot, fixture.Source.Package!.Archive.RelativePath);
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var metadata = fixture.SourceCodec.ReadMetadata(fixture.SourceReference);
            fixture.SourceCodec.RequireFrozenBinding(metadata, fixture.Plan);
            Assert.False(metadata.DesiredEnabled);
            Assert.Throws<IOException>(() => fixture.RequireContents(metadata));
        }
        fixture.RequireContents(fixture.SourceCodec.ReadMetadata(fixture.SourceReference));
    }

    [Theory(DisplayName = "Strict Private Source Shape Does Not Admit Unknown Or Incomplete Metadata")]
    [InlineData("unknown-member")]
    [InlineData("missing-settings")]
    [InlineData("null-package")]
    [InlineData("invalid-claim-mode")]
    public void RejectsUnknownMetadata(string fault)
    {
        using var fixture = new PayloadSourceFixture();
        var node = Read(fixture);
        switch (fault)
        {
            case "unknown-member": node["unexpected"] = true; break;
            case "missing-settings": node.Remove("settingsRevision"); break;
            case "null-package": node["package"] = null; break;
            default: node["claims"]![0]!["mode"] = 999; break;
        }
        var reference = fixture.Package.Blobs.PersistPlan(JsonSerializer.SerializeToUtf8Bytes(node));
        if (fault is "unknown-member" or "missing-settings")
            Assert.Throws<JsonException>(() => fixture.SourceCodec.ReadMetadata(reference));
        else Assert.Throws<IOException>(() => fixture.SourceCodec.ReadMetadata(reference));
        Assert.False(Directory.Exists(fixture.Plan.TargetPath));
    }

    [Theory(DisplayName = "A Changed Source Cannot Rebind An Original Accepted Package Or Frozen Operation")]
    [InlineData("generation")]
    [InlineData("entry-id")]
    [InlineData("manifest-sha")]
    [InlineData("operation")]
    [InlineData("instance")]
    public void RejectsChangedSources(string fault)
    {
        using var fixture = new PayloadSourceFixture();
        var node = Read(fixture);
        switch (fault)
        {
            case "generation": node["package"]!["catalog"]!["identity"]!["generationId"] = Guid.NewGuid(); break;
            case "entry-id": node["package"]!["entry"]!["extensionId"] = "other-plugin"; break;
            case "manifest-sha": node["package"]!["entry"]!["manifestSha256"] = new string('f', 64); break;
            case "operation": node["operationId"] = Guid.NewGuid(); break;
            default: node["instanceId"] = Guid.NewGuid(); break;
        }
        var reference = fixture.Package.Blobs.PersistPlan(JsonSerializer.SerializeToUtf8Bytes(node));
        Assert.Throws<IOException>(() => fixture.RequireContents(fixture.SourceCodec.ReadMetadata(reference)));
        Assert.False(Directory.Exists(fixture.Plan.TargetPath));
    }

    [Fact(DisplayName = "A Decoded Payload Source Does Not Permit A Later Read To Accept Changed Source Bytes")]
    public void RechecksDecodedSource()
    {
        using var fixture = new PayloadSourceFixture();
        var codec = new PayloadPlanCodec(fixture.Package.Catalog.Instance, fixture.Package.Catalog.Contracts,
            fixture.Package.Blobs, fixture.Roots);
        using var frozen = codec.Freeze(fixture.Plan);
        var plan = codec.ReadStructure(frozen.Reference, out var source);
        Assert.NotNull(source);
        Assert.Equal(fixture.Source.OperationId, source.OperationId);
        Assert.Equal(fixture.SourceReference, plan.ExtensionSource);
        fixture.SourceCodec.RequireFrozenBinding(source, plan);
        var path = Path.Combine(fixture.Package.Catalog.Instance.RuntimeRoot, fixture.SourceReference.RelativePath);
        var bytes = File.ReadAllBytes(path);
        var changed = bytes.ToArray(); changed[0] = (byte)'!';
        try
        {
            File.WriteAllBytes(path, changed);
            Assert.Throws<IOException>(() => codec.ReadStructure(frozen.Reference, out _));
            Assert.Throws<IOException>(() => fixture.SourceCodec.ReadMetadata(fixture.SourceReference));
            Assert.Equal(changed, File.ReadAllBytes(path));
        }
        finally { File.WriteAllBytes(path, bytes); }
        var reloaded = codec.ReadStructure(frozen.Reference, out var restored);
        Assert.NotNull(restored);
        fixture.SourceCodec.RequireFrozenBinding(restored, reloaded);
        Assert.Equal(plan.ExtensionSource, reloaded.ExtensionSource);
        Assert.Equal(source.SettingsRevision, restored.SettingsRevision);
        Assert.Equal(source.Claims.ToArray(), restored.Claims.ToArray());
    }

    private static JsonObject Read(PayloadSourceFixture fixture)
    {
        using var input = fixture.Package.Blobs.OpenVerified(fixture.SourceReference);
        using var bytes = new MemoryStream(); input.CopyTo(bytes);
        return JsonNode.Parse(bytes.ToArray())!.AsObject();
    }
}
