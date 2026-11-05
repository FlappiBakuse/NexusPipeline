using System.Security.Cryptography;
using System.Collections.Immutable;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Extensions;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class ProjectSourceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadsExactMetadata(bool guiAssets)
    {
        using var fixture = new ProjectSourceFixture();
        fixture.Write("interface.json", "{\"name\":\"MaaStellaSora\"}"u8.ToArray());
        var directory = Path.Combine(fixture.Path, guiAssets ? "Assets" : "assets");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "interface.json"), "{\"name\":\"nested\"}");
        using var source = fixture.Open();
        Assert.Contains("MaaStellaSora", System.Text.Encoding.UTF8.GetString(source.ReadMetadata("interface.json", default)!.Content.Span));
        var nested = source.ReadMetadata("assets/interface.json", default);
        if (guiAssets)
        {
            Assert.Null(nested);
            Assert.Throws<IOException>(() => source.Read("assets/interface.json", default));
        }
        else Assert.Contains("nested", System.Text.Encoding.UTF8.GetString(nested!.Content.Span));
        source.RequireCurrent();
        var captured = source.Freeze(default);
        Assert.Equal(guiAssets ? 1 : 2, captured.Files.Length);
    }

    [Theory(DisplayName = "Script Source Authority Reads Its Own Root Without An Account And Cannot Produce A Binding Execution Snapshot")]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadsIndependentScript(bool removeBindings)
    {
        using var fixture = new ProjectSourceFixture();
        fixture.Write("interface.json", "{\"interface_version\":2}"u8.ToArray());
        var owner = fixture.Catalog;
        var store = new AutomationDefinitionStore(owner.Database);
        var catalog = store.Read();
        if (removeBindings)
        {
            var removed = catalog.Bindings.Select(item => new RemoveAutomationDefinition(AutomationDefinitionKind.Binding, item.Id, item.Revision))
                .Concat(catalog.Users.Select(item => new RemoveAutomationDefinition(AutomationDefinitionKind.User, item.Id, item.Revision))).ToImmutableArray();
            var change = new AutomationChanges(catalog.Revision, [], [], [], [], [], [], removed);
            store.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", change), change, DateTimeOffset.UtcNow);
            catalog = store.Read(); Assert.Empty(catalog.Bindings); Assert.Empty(catalog.Users);
        }
        var script = Assert.Single(catalog.Scripts);
        using var source = SelectedProjectSource.ForScript(owner.Instance, owner.Database, owner.Lease, owner.Resources,
            script.Id, script.Revision, "maas", () => { });
        Assert.Equal(script.Id, source.Script.Id); Assert.Equal(script.Revision, source.Script.Revision);
        Assert.Equal("{\"interface_version\":2}"u8.ToArray(), source.Read("interface.json", default)!.Content.ToArray());
        Assert.Equal("project_binding_authority_required", Assert.Throws<IOException>(() => source.Freeze(default)).Message);
        source.RequireCurrent();
        var changes = new AutomationChanges(catalog.Revision, [], [script with { Name = "Changed" }], [], [], [], [], []);
        store.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", changes), changes, DateTimeOffset.UtcNow);
        Assert.Throws<IOException>(source.RequireCurrent);
    }

    [Theory(DisplayName = "Complete Native Resources Use The Existing Tree Archive Budget And Do Not Consume The Compiler Source File Budget")]
    [InlineData(false)]
    [InlineData(true)]
    public void ArchivesCompleteResources(bool changed)
    {
        using var fixture = new ProjectSourceFixture();
        var directory = Path.Combine(fixture.Path, "resource"); Directory.CreateDirectory(directory);
        var count = changed ? 140 : 2100;
        for (var index = 0; index < count; index++) File.WriteAllText(Path.Combine(directory, $"pipeline-{index}.json"), "{\"pipeline\":" + index + "}");
        using var source = fixture.Open();
        if (!changed)
        {
            var tree = new FileTreeScanner(SnapshotBudget.Default).CaptureArchive(directory, default);
            var claims = PhysicalResourceAdmission.ReadTree(directory, tree); Assert.True(claims.Length > 4096);
            var existing = PhysicalResourceAdmission.Read(fixture.Path, ResourceClaimMode.Reference, includeDescendants: false)
                .Select(item => item.ResourceKey).ToHashSet(StringComparer.Ordinal);
            var conflict = claims.Skip(4096).First(item => !existing.Contains(item.ResourceKey));
            var probe = claims.Take(4096).First(item => !existing.Contains(item.ResourceKey));
            using (var blocked = fixture.Catalog.Resources.TryAcquire(Guid.NewGuid(), [conflict with { Mode = ResourceClaimMode.Mutation }]).Lease!)
            {
                Assert.NotNull(blocked);
                Assert.Throws<IOException>(() => source.CaptureResource("resource", default));
                using var released = fixture.Catalog.Resources.TryAcquire(Guid.NewGuid(), [probe with { Mode = ResourceClaimMode.Mutation }]).Lease;
                Assert.NotNull(released);
            }
        }
        var resource = source.CaptureResource("resource", default);
        Assert.Equal(count, resource.Archive.Snapshot.Entries.Count(item => item.Kind == NexusPipeline.Core.Configuration.FileObjectKind.File));
        Assert.Equal(count, resource.Archive.Contents.Count);
        Assert.Throws<IOException>(() => File.Delete(Path.Combine(directory, "pipeline-0.json")));
        Assert.Throws<IOException>(() => File.WriteAllText(Path.Combine(directory, "pipeline-0.json"), "changed"));
        var captured = source.Freeze(default); Assert.Empty(captured.Files); Assert.Single(captured.ResourceTrees);
        foreach (var content in resource.Archive.Contents) new ImmutableBlobStore(fixture.Catalog.Instance, new()).Verify(content);
        if (changed)
        { File.WriteAllText(Path.Combine(directory, "late.json"), "{}"); Assert.Throws<IOException>(source.RequireCurrent); }
        else source.RequireCurrent();
    }

    [Fact(DisplayName = "A Selected Binding Captures Exact Physical Source Bytes Above The Ordinary Json Limit Without Exposing Mutable Owned Buffers")]
    public void CapturesSelectedSources()
    {
        using var fixture = new ProjectSourceFixture();
        var bytes = new byte[3 * 1024 * 1024]; Array.Fill(bytes, (byte)'x'); fixture.Write("large.json", bytes);
        using var source = fixture.Open();
        var read = source.Read("large.json", default)!; Assert.Equal(bytes, read.Content.ToArray());
        System.Runtime.InteropServices.MemoryMarshal.AsMemory(read.Content).Span.Fill(0);
        Assert.Equal(bytes, source.Read("large.json", default)!.Content.ToArray());
        Assert.Null(source.Read("missing.json", default));
        var captured = source.Freeze(default); var file = Assert.Single(captured.Files);
        Assert.Equal(fixture.Binding, captured.BindingId); Assert.Equal("large.json", file.Path);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), file.Content.Sha256);
        using var stored = new ImmutableBlobStore(fixture.Catalog.Instance, new()).OpenVerified(file.Content);
        Assert.Equal(bytes.LongLength, stored.Length); Assert.Equal(file.Content.Sha256, Convert.ToHexStringLower(SHA256.HashData(stored)));
        Assert.Throws<InvalidOperationException>(() => source.Read("large.json", default)); source.RequireCurrent();
    }

    [Theory(DisplayName = "Selected Source Directory And Negative Reads Detect New Files Directories Or Replaced Unread Objects")]
    [InlineData("file")]
    [InlineData("directory")]
    [InlineData("replacement")]
    [InlineData("missing-parent")]
    public void RejectsChangedListing(string change)
    {
        using var fixture = new ProjectSourceFixture(); fixture.Write("unread.json", "{}"u8.ToArray());
        using var source = fixture.Open();
        Assert.Null(source.Read(change == "missing-parent" ? "new/file.json" : "missing.json", default));
        _ = source.Freeze(default);
        if (change == "directory" || change == "missing-parent") Directory.CreateDirectory(Path.Combine(fixture.Path, "new"));
        else if (change == "replacement")
        {
            File.Move(Path.Combine(fixture.Path, "unread.json"), Path.Combine(fixture.Path, "previous.json"));
            fixture.Write("unread.json", "{}"u8.ToArray());
        }
        else fixture.Write("missing.json", "{}"u8.ToArray());
        Assert.Throws<IOException>(source.RequireCurrent);
    }

    [Fact(DisplayName = "Selected Source File And Parent Handles Plus Physical Reference Claims Remain Held Through Capture")]
    public void HoldsReadFiles()
    {
        using var fixture = new ProjectSourceFixture(); fixture.Write("interface.json", "{}"u8.ToArray());
        using var source = fixture.Open(); Assert.NotNull(source.Read("interface.json", default));
        Assert.Null(source.Read("missing.json", default));
        Assert.Null(fixture.Catalog.Resources.TryAcquire(Guid.NewGuid(), PhysicalResourceAdmission.Read(Path.Combine(fixture.Path, "interface.json"))).Lease);
        Assert.Null(fixture.Catalog.Resources.TryAcquire(Guid.NewGuid(), PhysicalResourceAdmission.Read(Path.Combine(fixture.Path, "missing.json"))).Lease);
        Assert.Throws<IOException>(() => File.WriteAllText(Path.Combine(fixture.Path, "interface.json"), "changed"));
        Assert.Throws<IOException>(() => File.Delete(Path.Combine(fixture.Path, "interface.json")));
        Assert.Throws<IOException>(() => Directory.Move(fixture.Path, fixture.Path + "-moved"));
        _ = source.Freeze(default); source.RequireCurrent();
        source.Dispose(); fixture.Write("interface.json", "changed"u8.ToArray());
    }

    [Theory(DisplayName = "Source Reads Reject Absolute Traversal Stream Backslash And Case Alias Paths")]
    [InlineData("../outside.json")]
    [InlineData("D:/outside.json")]
    [InlineData("interface.json:stream")]
    [InlineData("config\\file.json")]
    [InlineData("INTERFACE.JSON")]
    [InlineData("")]
    public void RejectsForeignPaths(string path)
    {
        using var fixture = new ProjectSourceFixture(); fixture.Write("interface.json", "{}"u8.ToArray());
        using var source = fixture.Open(); Assert.Throws<IOException>(() => source.Read(path, default));
    }

    [Fact(DisplayName = "Changed Binding Or Revoked Caller Invalidates A Captured Source Instead Of Reusing Old Authority")]
    public void RejectsChangedBinding()
    {
        using var fixture = new ProjectSourceFixture(); var revoked = false;
        using var source = fixture.Open(() => { if (revoked) throw new UnauthorizedAccessException(); });
        _ = source.List(""); revoked = true;
        Assert.Throws<UnauthorizedAccessException>(source.RequireCurrent); revoked = false;
        var store = new AutomationDefinitionStore(fixture.Catalog.Database); var catalog = store.Read();
        var binding = catalog.Bindings.Single(item => item.Id == fixture.Binding);
        var changes = new AutomationChanges(catalog.Revision, [], [], [], [binding with { Priority = binding.Priority + 1 }], [], [], []);
        store.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", changes), changes, DateTimeOffset.UtcNow);
        Assert.Throws<IOException>(source.RequireCurrent);
    }

    [Theory(DisplayName = "A Source Enforces Its Own File Count And Per File Budget Before Freezing Content")]
    [InlineData(false)]
    [InlineData(true)]
    public void BoundsSourceInputs(bool large)
    {
        using var fixture = new ProjectSourceFixture();
        if (large)
        { using var file = File.Create(Path.Combine(fixture.Path, "large.json")); file.SetLength(8 * 1024 * 1024 + 1); }
        else for (var index = 0; index < 129; index++) fixture.Write($"file-{index}.json", "{}"u8.ToArray());
        using var source = fixture.Open();
        if (large) Assert.Throws<IOException>(() => source.Read("large.json", default));
        else
        {
            for (var index = 0; index < 128; index++) Assert.NotNull(source.Read($"file-{index}.json", default));
            Assert.Throws<IOException>(() => source.Read("file-128.json", default));
        }
    }

}
