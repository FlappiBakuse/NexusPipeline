using System.Text.Json;
using NexusPipeline.Extensibility.Compilation;
using NexusPipeline.Platform.Storage.Extensions;
using Xunit;

namespace NexusPipeline.ContractTests;

public sealed class CompilationGraphTests
{
    [Fact(DisplayName = "Initialization Occurrences Are Ordered Frozen Unique Technical Identities")]
    public void FreezesInitializationOccurrences()
    {
        var ids = new[] { "prepare-second", "prepare-first" };
        var input = new CompilationResult("index", [new("index", CompiledAssetKind.Index, "{}"u8.ToArray(), [])],
            JsonSerializer.SerializeToElement(new { })) { InitializationOccurrences = ids };
        var frozen = CompilationGraph.Freeze(input);
        ids[0] = "changed";
        Assert.Equal(new[] { "prepare-second", "prepare-first" }, frozen.InitializationOccurrences);
        foreach (var invalid in new[] { new[] { "same", "same" }, new[] { "" }, new[] { "line\nbreak" },
            Enumerable.Range(0, 129).Select(value => "task-" + value).ToArray() })
            Assert.Throws<IOException>(() => CompilationGraph.Freeze(input with { InitializationOccurrences = invalid }));
    }

    [Theory(DisplayName = "Only The Explicit Browser Index Subgraph Can Expose Index And Page Assets")]
    [InlineData("valid")]
    [InlineData("absent")]
    [InlineData("missing")]
    [InlineData("page")]
    [InlineData("artifact")]
    [InlineData("indirect")]
    public void BindsPreviewGraph(string mode)
    {
        var assets = new[] { new CompiledAsset("root", CompiledAssetKind.Index, "{}"u8.ToArray(), ["public", "private"]),
            new CompiledAsset("public", CompiledAssetKind.Index, "{}"u8.ToArray(), mode == "indirect" ? ["page", "private"] : ["page"]),
            new CompiledAsset("page", CompiledAssetKind.Page, "[]"u8.ToArray(), []),
            new CompiledAsset("private", CompiledAssetKind.Artifact, "private"u8.ToArray(), []) };
        var preview = mode switch { "absent" => null, "missing" => "missing", "page" => "page", "artifact" => "private", _ => "public" };
        var input = new CompilationResult("root", assets, JsonSerializer.SerializeToElement(new { })) { PreviewIndex = preview };
        if (mode is not ("valid" or "absent")) { Assert.Throws<IOException>(() => CompilationGraph.Freeze(input)); return; }
        var frozen = CompilationGraph.Freeze(input); Assert.Equal(preview, frozen.PreviewIndex);
        var names = CompilationGraph.RequirePreview(frozen.PreviewIndex, frozen.Assets.Select(item => (item.Name, item.Kind, item.References)));
        Assert.Equal(mode == "valid" ? 2 : 0, names.Count); Assert.DoesNotContain("private", names); Assert.DoesNotContain("root", names);
    }

    [Theory(DisplayName = "Readonly Compilations Retain The Same Complete Graph And An Independent Host Execution Gate")]
    [InlineData(false)]
    [InlineData(true)]
    public void FreezesExecutionGate(bool executable)
    {
        var input = new CompilationResult("index", [new("index", CompiledAssetKind.Index, "{}"u8.ToArray(), [])],
            JsonSerializer.SerializeToElement(new { canExecute = !executable })) { CanExecute = executable };
        var frozen = CompilationGraph.Freeze(input);
        Assert.Equal(executable, frozen.CanExecute); Assert.Single(frozen.Assets);
        Assert.Equal(!executable, frozen.ExecutionView.GetProperty("canExecute").GetBoolean());
    }

    [Theory(DisplayName = "Compiler Resource Trees Are Explicit Bounded Relative Declarations With Independent Frozen Ownership")]
    [InlineData("valid")]
    [InlineData("absolute")]
    [InlineData("traversal")]
    [InlineData("duplicate")]
    [InlineData("limit")]
    public void BindsResourceDeclarations(string changed)
    {
        var resources = new[] { new SourceResourceTree("runtime", "assets/resource") };
        var input = new CompilationResult("index", [new("index", CompiledAssetKind.Index, "{}"u8.ToArray(), [])], JsonSerializer.SerializeToElement(new { }))
            { ResourceTrees = resources };
        if (changed == "absolute") input = input with { ResourceTrees = [resources[0] with { Path = "D:/outside" }] };
        if (changed == "traversal") input = input with { ResourceTrees = [resources[0] with { Path = "../outside" }] };
        if (changed == "duplicate") input = input with { ResourceTrees = [resources[0], resources[0]] };
        if (changed == "limit") input = input with { ResourceTrees = Enumerable.Range(0, 9).Select(index => new SourceResourceTree("tree-" + index, "resource-" + index)).ToArray() };
        if (changed != "valid") { Assert.Throws<IOException>(() => CompilationGraph.Freeze(input)); return; }
        var result = CompilationGraph.Freeze(input); resources[0] = resources[0] with { Path = "changed" };
        Assert.Equal("assets/resource", Assert.Single(result.ResourceTrees).Path);
    }

    [Fact(DisplayName = "A Compiler Result Owns Independent Complete Content And Execution Json After Caller Disposal Or Mutation")]
    public void FreezesOwnedContent()
    {
        using var json = JsonDocument.Parse("{\"task\":\"one\"}"); var content = "{\"body\":7}"u8.ToArray();
        var references = new[] { "artifact" };
        var assets = new[] { new CompiledAsset("index", CompiledAssetKind.Index, "{}"u8.ToArray(), references),
            new CompiledAsset("artifact", CompiledAssetKind.Artifact, content, []) };
        var frozen = CompilationGraph.Freeze(new("index", assets, json.RootElement));
        Array.Fill(content, (byte)0); references[0] = "foreign"; json.Dispose();
        Assert.Equal("one", frozen.ExecutionView.GetProperty("task").GetString());
        Assert.Equal("artifact", frozen.Assets[0].References[0]); Assert.Equal("{\"body\":7}"u8.ToArray(), frozen.Assets[1].Content);
    }

    [Theory(DisplayName = "Compiled Content Must Form One Complete Closed Acyclic Graph With Exact Case Sensitive Names")]
    [InlineData("missing")]
    [InlineData("orphan")]
    [InlineData("cycle")]
    [InlineData("duplicate")]
    [InlineData("alias")]
    [InlineData("root")]
    [InlineData("kind")]
    [InlineData("references")]
    public void RejectsBrokenGraphs(string changed)
    {
        var assets = new List<CompiledAsset> { new("index", CompiledAssetKind.Index, "{}"u8.ToArray(), ["page"]),
            new("page", CompiledAssetKind.Page, "[]"u8.ToArray(), []) };
        switch (changed)
        {
            case "missing": assets.RemoveAt(1); break;
            case "orphan": assets.Add(new("orphan", CompiledAssetKind.Artifact, new byte[] { 0 }, [])); break;
            case "cycle": assets[1] = assets[1] with { References = ["index"] }; break;
            case "duplicate": assets.Add(assets[1]); break;
            case "alias": assets[0] = assets[0] with { References = ["PAGE"] }; break;
            case "kind": assets[1] = assets[1] with { Kind = (CompiledAssetKind)99 }; break;
            case "references": assets[0] = assets[0] with { References = ["page", "page"] }; break;
        }
        Assert.Throws<IOException>(() => CompilationGraph.Freeze(new(changed == "root" ? "page" : "index", assets, JsonSerializer.SerializeToElement(new { }))));
    }

    [Theory(DisplayName = "Index Pages Execution Views And Complete Artifact Bytes Have Independent Host Limits")]
    [InlineData("page")]
    [InlineData("index")]
    [InlineData("view")]
    [InlineData("artifact")]
    public void BoundsCompiledContent(string changed)
    {
        var assets = new List<CompiledAsset> { new("index", CompiledAssetKind.Index,
            changed == "index" ? new byte[524289] : "{}"u8.ToArray(), ["asset"]),
            new("asset", changed == "artifact" ? CompiledAssetKind.Artifact : CompiledAssetKind.Page,
                changed == "artifact" ? new byte[32 * 1024 * 1024 + 1] : changed == "page" ? new byte[65537] : "[]"u8.ToArray(), []) };
        var view = JsonSerializer.SerializeToElement(new { text = changed == "view" ? new string('x', 262144) : "" });
        Assert.Throws<IOException>(() => CompilationGraph.Freeze(new("index", assets, view)));
    }
}
