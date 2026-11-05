using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Compilation;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Extensions;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ProjectSubsetTests
{
    [Fact(DisplayName = "A Root Program Subset Archives Exact Files And Directories With Real Physical Identities While Unselected Configuration Remains Writable")]
    public void CapturesRootSubset()
    {
        using var fixture = new ProjectSourceFixture(configuration: true); WritePrograms(fixture);
        using var source = fixture.Open(); var archive = source.CaptureResource("", ["python.exe", "Agent"], default).Archive;
        Assert.Equal(PhysicalObjectIdentity.Read(fixture.Path), archive.OriginalIdentity);
        Assert.Equal(new[] { "", "Agent", "Agent/run.py", "python.exe" }, archive.Snapshot.Entries.Select(value => value.RelativePath));
        foreach (var entry in archive.Snapshot.Entries)
            Assert.Equal(PhysicalObjectIdentity.Read(entry.RelativePath.Length == 0 ? fixture.Path : Path.Combine(fixture.Path, entry.RelativePath)),
                archive.OriginalObjects.Single(value => value.RelativePath == entry.RelativePath).Identity);
        File.WriteAllText(fixture.ConfigurationFile!, "changed config"); fixture.Write("unselected.txt", "changed outside"u8.ToArray());
        source.RequireCurrent(); var captured = source.Freeze(default); Assert.Empty(captured.Files);
        Assert.Throws<IOException>(() => fixture.Write("python.exe", [9]));
        var output = Path.Combine(Path.GetDirectoryName(fixture.Path)!, "subset-materialized");
        new FileTreeArchive(new(SnapshotBudget.Default), new(fixture.Catalog.Instance, new())).MaterializeNew(archive, output, default);
        Assert.Equal(File.ReadAllBytes(Path.Combine(fixture.Path, "Agent/run.py")), File.ReadAllBytes(Path.Combine(output, "Agent/run.py")));
        Assert.False(Directory.Exists(Path.Combine(output, "config"))); Assert.False(File.Exists(Path.Combine(output, "unselected.txt")));
        File.WriteAllText(Path.Combine(fixture.Path, "Agent", "late.py"), "changed included tree"); Assert.Throws<IOException>(source.RequireCurrent);
    }

    [Fact(DisplayName = "Subset Declarations Are Deep Frozen And Refuse Empty Absolute Overlapping Case Aliased Or Out Of Scope Entries")]
    public void RequiresFiniteSubset()
    {
        using var fixture = new ProjectSourceFixture(); WritePrograms(fixture); using var source = fixture.Open();
        foreach (var paths in new[] { Array.Empty<string>(), new[] { "../outside" }, new[] { "D:/outside" }, new[] { "Agent", "agent" }, new[] { "Agent", "Agent/run.py" }, new[] { "" } })
            Assert.Throws<IOException>(() => source.CaptureResource("", paths, default));
        Assert.Throws<IOException>(() => source.CaptureResource("", default));
        var included = new[] { "python.exe", "Agent" }; using var view = JsonDocument.Parse("{}");
        var result = new CompilationResult("index", [new("index", CompiledAssetKind.Index, "{}"u8.ToArray(), [])], view.RootElement)
            { ResourceTrees = [new("programs", "") { IncludedPaths = included }] };
        var frozen = CompilationGraph.Freeze(result); included[0] = "unselected.txt";
        Assert.Equal(new[] { "Agent", "python.exe" }, frozen.ResourceTrees.Single().IncludedPaths);
        var archive = source.CaptureResource("", frozen.ResourceTrees.Single().IncludedPaths, default).Archive;
        var extra = archive with { Snapshot = archive.Snapshot with { Entries = archive.Snapshot.Entries.Append(new("config", FileObjectKind.Directory, 0, null)).ToArray() } };
        Assert.Throws<IOException>(() => ProjectSubsetPolicy.RequireClosure(extra, frozen.ResourceTrees.Single().IncludedPaths!));
    }

    [Theory(DisplayName = "The Actual Installed Compiler Persists A Finite Root Program Tree Through The Compiled Snapshot And Exact Data Plane While Refusing Real Configuration Overlap")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreezesCompiledSubset(bool overlap)
    {
        await using var fixture = new ProjectSaveFixture(configuration: true); await fixture.StartAsync(); WritePrograms(fixture.Project);
        var included = overlap ? new[] { "python.exe", "config" } : new[] { "python.exe", "Agent" };
        var selection = JsonSerializer.SerializeToElement(new { resourcePath = "", includedPaths = included });
        var catalog = fixture.Project.Catalog;
        var work = fixture.Service.UseAsync(fixture.Project.Binding, 1, "maas", selection, () => { }, (snapshot, current, _) =>
        {
            current(); var blobs = new ImmutableBlobStore(catalog.Instance, catalog.Contracts);
            var restored = CompiledProjectCodec.Read(catalog.Instance, blobs, snapshot.Document);
            var tree = Assert.Single(restored.Value.ResourceTrees); Assert.Equal("", tree.Path); Assert.NotNull(tree.IncludedPaths);
            Assert.Equal(new[] { "Agent", "python.exe" }, tree.IncludedPaths ?? throw new IOException("fixture_subset_missing"));
            using var input = new ProviderProjectInput(blobs, restored);
            var manifest = input.Read(new() { Fingerprint = snapshot.Document.Sha256, Sequence = 1, Kind = "manifest" });
            Assert.Equal("", Assert.Single(manifest.Trees!).Path);
            var contents = input.Read(new() { Fingerprint = snapshot.Document.Sha256, Sequence = 2, Kind = "file", Name = tree.Name,
                Path = "Agent/run.py", Offset = 0, MaximumBytes = 4096 });
            Assert.Equal("print('frozen')", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(contents.ContentBase64!)));
            Assert.Throws<IOException>(() => input.Read(new() { Fingerprint = snapshot.Document.Sha256, Sequence = 3, Kind = "file", Name = tree.Name,
                Path = "config/local.json", Offset = 0, MaximumBytes = 4096 }));
            var altered = restored.Value with { ResourceTrees = [tree with { IncludedPaths = null }] };
            Assert.Throws<IOException>(() => CompiledProjectCodec.Read(catalog.Instance, blobs, blobs.PersistPlan(JsonSerializer.SerializeToUtf8Bytes(altered))));
            return ValueTask.FromResult(true);
        }, default).AsTask();
        if (overlap) Assert.Equal("project_resource_configuration_overlap", (await Assert.ThrowsAsync<IOException>(() => work)).Message);
        else Assert.True(await work);
    }

    private static void WritePrograms(ProjectSourceFixture fixture)
    {
        Directory.CreateDirectory(Path.Combine(fixture.Path, "Agent")); fixture.Write("python.exe", [1, 2, 3, 4]);
        fixture.Write("Agent/run.py", "print('frozen')"u8.ToArray()); fixture.Write("unselected.txt", "not a program input"u8.ToArray());
    }
}
