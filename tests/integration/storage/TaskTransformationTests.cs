using System.Collections.Immutable;
using System.Text;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class TaskTransformationTests
{
    [Theory(DisplayName = "Run Task Copies Select And Restore Stable Ids While Preserving Progress And Unlisted Bytes")]
    [InlineData("objects", ConfigurationSyntax.Json)]
    [InlineData("objects", ConfigurationSyntax.JsonWithComments)]
    [InlineData("objects", ConfigurationSyntax.Yaml)]
    [InlineData("map", ConfigurationSyntax.Json)]
    [InlineData("map", ConfigurationSyntax.JsonWithComments)]
    [InlineData("map", ConfigurationSyntax.Yaml)]
    [InlineData("parallel", ConfigurationSyntax.Json)]
    [InlineData("parallel", ConfigurationSyntax.JsonWithComments)]
    [InlineData("parallel", ConfigurationSyntax.Yaml)]
    public void PreservesTaskProgress(string shape, ConfigurationSyntax syntax)
    {
        var original = Text(shape, syntax, progress: false);
        var progress = Text(shape, syntax, progress: true);
        var expected = progress.Replace("\"A\",\"enabled\":false", "\"A\",\"enabled\":true", StringComparison.Ordinal)
            .Replace("\"B\",\"enabled\":true", "\"B\",\"enabled\":false", StringComparison.Ordinal)
            .Replace("\"B\":true,\"A\":false", "\"B\":false,\"A\":true", StringComparison.Ordinal)
            .Replace("[false,true,false]", "[false,false,true]", StringComparison.Ordinal);
        using var fixture = new Fixture(original);
        var rule = Rule(shape, syntax);
        var captured = fixture.Transform.Capture(fixture.Operation, fixture.Source, [rule]);
        Assert.True(captured[0].Initial.Values["A"]);
        Assert.False(captured[0].Initial.Values["B"]);
        var selected = fixture.Transform.Select(fixture.Operation, fixture.Source, captured);
        Assert.Equal(2, fixture.Reads);
        Assert.NotEqual(original, fixture.Read(selected));
        var progressTree = fixture.Tree(progress);
        Assert.NotEqual(fixture.Source.Tree.Entries[0].Sha256, progressTree.Entries[0].Sha256);
        var restored = fixture.Transform.Restore(fixture.Operation, fixture.Source, captured, progressTree);
        Assert.Equal(4, fixture.Reads);
        Assert.Equal(Encoding.UTF8.GetBytes(expected), fixture.ReadBytes(restored));
        Assert.Equal(original, fixture.Read(fixture.Source.Tree));
        Assert.Equal(progress, fixture.Read(progressTree));
        Assert.Equal(Encoding.UTF8.GetBytes(original), File.ReadAllBytes(fixture.ExternalPath));
        Assert.Equal(restored.Entries, fixture.Transform.Restore(fixture.Operation, fixture.Source, captured, restored).Entries);
    }

    [Fact(DisplayName = "Empty Selected Ids Explicitly Disable Every Authorized Task Without Changing Other Tasks")]
    public void DisablesEmptySelections()
    {
        using var fixture = new Fixture("{\"tasks\":{\"A\":true,\"B\":true,\"other\":true}}");
        var rule = Rule("map", ConfigurationSyntax.Json) with { SelectedIds = [] };
        var captured = fixture.Transform.Capture(fixture.Operation, fixture.Source, [rule]);
        var selected = fixture.Transform.Select(fixture.Operation, fixture.Source, captured);
        Assert.Equal("{\"tasks\":{\"A\":false,\"B\":false,\"other\":true}}", fixture.Read(selected));
        Assert.Equal(fixture.Source.Tree.Entries, fixture.Transform.Restore(fixture.Operation, fixture.Source, captured, selected).Entries);
    }

    [Theory(DisplayName = "Initial Task Facts Cannot Be Transplanted Or Forged Before A Selected Blob Is Published")]
    [InlineData("operation")]
    [InlineData("resource")]
    [InlineData("revision")]
    [InlineData("sha")]
    [InlineData("declaration")]
    [InlineData("values")]
    public void RejectsForgedFacts(string change)
    {
        using var fixture = new Fixture("{\"tasks\":{\"A\":true,\"B\":false},\"other\":{\"A\":true,\"B\":false}}");
        var captured = fixture.Transform.Capture(fixture.Operation, fixture.Source, [Rule("map", ConfigurationSyntax.Json)]);
        var initial = captured[0].Initial;
        initial = change switch
        {
            "operation" => initial with { Source = initial.Source with { OperationId = Guid.NewGuid() } },
            "resource" => initial with { Source = initial.Source with { ResourceId = "other" } },
            "revision" => initial with { Source = initial.Source with { Revision = 2 } },
            "sha" => initial with { Source = initial.Source with { SourceSha256 = new('e', 64) } },
            "declaration" => initial with { Declaration = initial.Declaration with { Source = new ConfigurationMapTasks(Selector("other")) } },
            _ => initial with { Values = initial.Values.SetItem("A", false) }
        };
        var before = fixture.ContentFiles();
        Assert.Throws<ConfigurationException>(() => fixture.Transform.Select(fixture.Operation, fixture.Source,
            [captured[0] with { Initial = initial }]));
        Assert.Equal(before, fixture.ContentFiles());
    }

    [Fact(DisplayName = "Equivalent Reconstructed Declarations Remain Bound Without Immutable Array Reference Equality")]
    public void AcceptsEquivalentDeclarations()
    {
        using var fixture = new Fixture("{\"tasks\":{\"A\":true,\"B\":false}}");
        var captured = fixture.Transform.Capture(fixture.Operation, fixture.Source, [Rule("map", ConfigurationSyntax.Json)]);
        var item = captured[0] with { Initial = captured[0].Initial with { Declaration = Rule("map", ConfigurationSyntax.Json).Declaration } };
        fixture.Transform.ValidateInitial(fixture.Operation, fixture.Source, [item]);
        Assert.Contains("\"A\":false", fixture.Read(fixture.Transform.Select(fixture.Operation, fixture.Source, [item])));
    }

    [Theory(DisplayName = "Selected Tree Validation Never Publishes Content When Accepting Or Rejecting A Candidate")]
    [InlineData(false)]
    [InlineData(true)]
    public void ValidatesWithoutPublication(bool matching)
    {
        using var fixture = new Fixture("{\"tasks\":{\"A\":true,\"B\":false}}");
        var captured = fixture.Transform.Capture(fixture.Operation, fixture.Source, [Rule("map", ConfigurationSyntax.Json)]);
        var candidate = matching ? fixture.Tree("{\"tasks\":{\"A\":false,\"B\":true}}") : fixture.Source.Tree;
        var before = fixture.ContentFiles();
        if (matching) fixture.Transform.ValidateSelected(fixture.Operation, fixture.Source, captured, candidate);
        else Assert.Equal("configuration_run_selected_tasks_changed", Assert.Throws<ConfigurationException>(() =>
            fixture.Transform.ValidateSelected(fixture.Operation, fixture.Source, captured, candidate)).Message);
        Assert.Equal(before, fixture.ContentFiles());
    }

    [Fact(DisplayName = "Restored Directory Copies Preserve Independent Files And Current Counters")]
    public void PreservesIndependentFiles()
    {
        using var fixture = new Fixture("{\"tasks\":{\"A\":true,\"B\":false},\"count\":1}");
        var sourceFile = fixture.Source.Tree.Entries.Single() with { RelativePath = "tasks.json" };
        var other = fixture.Tree("unparsed independent bytes").Entries.Single() with { RelativePath = "other.bin" };
        var source = fixture.Source with { Tree = new(FileObjectKind.Directory,
            [new("", FileObjectKind.Directory, 0, null), sourceFile, other]) };
        var rule = Rule("map", ConfigurationSyntax.Json) with { RelativePath = "tasks.json" };
        var captured = fixture.Transform.Capture(fixture.Operation, source, [rule]);
        var current = fixture.Tree("{\"tasks\":{\"A\":false,\"B\":true,\"new\":true},\"count\":49}").Entries.Single() with { RelativePath = "tasks.json" };
        var progress = source.Tree with { Entries = [source.Tree.Entries[0], current, other] };
        var restored = fixture.Transform.Restore(fixture.Operation, source, captured, progress);
        Assert.Equal(other, restored.Entries.Single(entry => entry.RelativePath == "other.bin"));
        Assert.Equal("{\"tasks\":{\"A\":true,\"B\":false,\"new\":true},\"count\":49}", fixture.Read(restored, "tasks.json"));
    }

    [Fact(DisplayName = "Restoring Current Progress Still Enforces Write And Protected Selectors")]
    public void EnforcesProtectedSelectors()
    {
        using var fixture = new Fixture("{\"tasks\":{\"A\":true,\"B\":false}}");
        var rule = Rule("map", ConfigurationSyntax.Json);
        var denied = rule with { Access = rule.Access with { Write = [new(Selector("tasks", "A"), false)] } };
        var captured = fixture.Transform.Capture(fixture.Operation, fixture.Source, [denied]);
        var progress = fixture.Tree("{\"tasks\":{\"A\":false,\"B\":true}}");
        var before = fixture.ContentFiles();
        Assert.Throws<ConfigurationException>(() => fixture.Transform.Restore(fixture.Operation, fixture.Source, captured, progress));
        Assert.Equal(before, fixture.ContentFiles());
        Assert.Throws<ConfigurationException>(() => fixture.Transform.Capture(fixture.Operation, fixture.Source,
            [rule with { Access = rule.Access with { Protected = [Selector("tasks", "B")] } }]));
    }

    [Theory(DisplayName = "Explicit Task Projections Restore Original Switches While Retaining Current Progress Bytes")]
    [InlineData(ConfigurationSyntax.Json)]
    [InlineData(ConfigurationSyntax.JsonWithComments)]
    [InlineData(ConfigurationSyntax.Yaml)]
    public void RestoresExplicitTargets(ConfigurationSyntax syntax)
    {
        var original = "{\"root_enable\":true,\"nested\":{\"leaf_enable\":false},\"counter\":1,\"unknown\":true}";
        var progress = "{\"root_enable\":false,\"nested\":{\"leaf_enable\":true},\"counter\":71,\"unknown\":false}";
        if (syntax == ConfigurationSyntax.Yaml) { original = "# keep\r\n" + original; progress = "# keep\r\n" + progress; }
        using var fixture = new Fixture(original);
        var declaration = new ConfigurationTaskDeclaration(new ConfigurationSelectorTasks(
            [new("plugin:root", Selector("root_enable")), new("plugin:leaf", Selector("nested", "leaf_enable"))]), ["plugin:root", "plugin:leaf"]);
        var rule = Rule("map", syntax) with { Declaration = declaration, SelectedIds = ["plugin:leaf"] };
        var captured = fixture.Transform.Capture(fixture.Operation, fixture.Source, [rule]);
        var selected = fixture.Transform.Select(fixture.Operation, fixture.Source, captured);
        Assert.Equal(original.Replace("root_enable\":true", "root_enable\":false", StringComparison.Ordinal)
            .Replace("leaf_enable\":false", "leaf_enable\":true", StringComparison.Ordinal), fixture.Read(selected));
        var reconstructed = declaration with { Source = new ConfigurationSelectorTasks(
            [new("plugin:leaf", Selector("nested", "leaf_enable")), new("plugin:root", Selector("root_enable"))]) };
        captured = [captured[0] with { Initial = captured[0].Initial with { Declaration = reconstructed } }];
        fixture.Transform.ValidateInitial(fixture.Operation, fixture.Source, captured);
        var restored = fixture.Transform.Restore(fixture.Operation, fixture.Source, captured, fixture.Tree(progress));
        Assert.Equal(progress.Replace("root_enable\":false", "root_enable\":true", StringComparison.Ordinal)
            .Replace("leaf_enable\":true", "leaf_enable\":false", StringComparison.Ordinal), fixture.Read(restored));
        Assert.Equal(Encoding.UTF8.GetBytes(original), File.ReadAllBytes(fixture.ExternalPath));
    }

    private static string Text(string shape, ConfigurationSyntax syntax, bool progress)
    {
        var body = (shape, progress) switch
        {
            ("objects", false) => "\"tasks\":[{\"id\":\"A\",\"enabled\":true,\"count\":1},{\"id\":\"B\",\"enabled\":false}]",
            ("objects", true) => "\"tasks\":[{\"id\":\"new\",\"enabled\":false},{\"id\":\"B\",\"enabled\":true},{\"id\":\"A\",\"enabled\":false,\"count\":23}]",
            ("map", false) => "\"tasks\":{\"A\":true,\"B\":false}",
            ("map", true) => "\"tasks\":{\"new\":false,\"B\":true,\"A\":false}",
            (_, false) => "\"ids\":[\"A\",\"B\"],\"states\":[true,false]",
            _ => "\"ids\":[\"new\",\"B\",\"A\"],\"states\":[false,true,false]"
        };
        var text = "{ " + body + ", \"counter\": " + (progress ? "71" : "1") + ", \"note\": \"中文😀\" }\r\n";
        return syntax switch
        {
            ConfigurationSyntax.JsonWithComments => "\ufeff// keep current spelling\r\n" + text.Replace("{ ", "{ /* keep */ ", StringComparison.Ordinal),
            ConfigurationSyntax.Yaml => "\ufeff# keep current spelling\r\n" + text.TrimEnd('\r', '\n') + " # trailing\r\n",
            _ => text
        };
    }

    private static RunTaskRule Rule(string shape, ConfigurationSyntax syntax) => new("", syntax,
        new([new(new([]), true)], [new(new([]), true)], []), new(shape switch
        {
            "objects" => new ConfigurationObjectTasks(Selector("tasks"), "id", "enabled"),
            "map" => new ConfigurationMapTasks(Selector("tasks")),
            _ => new ConfigurationParallelTasks(Selector("ids"), Selector("states"))
        }, ["A", "B"]), ["B"]);
    private static ConfigurationSelector Selector(params string[] properties) =>
        new(properties.Select(property => (ConfigurationSelectorStep)new ConfigurationProperty(property)).ToImmutableArray());

    private sealed class Fixture : IDisposable
    {
        internal Guid Operation { get; } = Guid.NewGuid();
        internal RuntimeInstance Instance { get; }
        internal ImmutableBlobStore Blobs { get; }
        internal ConfigurationTaskTransformation Transform { get; }
        internal ConfigurationRevision Source { get; }
        internal string ExternalPath { get; }
        internal int Reads { get; private set; }
        internal Fixture(string text)
        {
            var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "run-tasks-" + Guid.NewGuid().ToString("N"));
            Instance = new InstanceRoot(new()).OpenOrCreate(Path.Combine(root, "runtime"));
            Blobs = new(Instance, new()); Transform = new(Blobs) { DocumentRead = () => Reads++ };
            Source = new(new(Guid.NewGuid(), "main"), 1, Guid.NewGuid(), Guid.NewGuid(), new(1, new('a', 64), null, false), Tree(text));
            ExternalPath = Path.Combine(root, "live-target.json"); File.WriteAllText(ExternalPath, text);
        }
        internal FileTreeSnapshot Tree(string text)
        {
            var blob = Blobs.PersistContent(Encoding.UTF8.GetBytes(text), default);
            return new(FileObjectKind.File, [new("", FileObjectKind.File, blob.SizeBytes, blob.Sha256)]);
        }
        internal string Read(FileTreeSnapshot tree, string path = "") => Encoding.UTF8.GetString(ReadBytes(tree, path));
        internal byte[] ReadBytes(FileTreeSnapshot tree, string path = "")
        {
            var entry = tree.Entries.Single(entry => entry.RelativePath == path);
            using var input = Blobs.OpenVerified(new($"blobs/content/{entry.Sha256}.bin", entry.SizeBytes, entry.Sha256!));
            var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes); return bytes;
        }
        internal string[] ContentFiles() => Directory.GetFiles(Path.Combine(Instance.RuntimeRoot, "blobs/content")).Order(StringComparer.Ordinal).ToArray();
        public void Dispose() { }
    }
}
