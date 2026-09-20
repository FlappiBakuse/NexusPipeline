using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NexusPipeline.Architecture;
using Xunit;

namespace NexusPipeline.Architecture.Tests;

public sealed class ArchitectureRulesTests
{
    [Fact]
    public void BackendMapUsesRepositoryLfRegardlessOfPlatform()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nxp-map-newline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "map.json");
            var value = new { schemaVersion = 2, files = new[] { "src/Test.cs" } };
            BackendMapWriter.Write(value, path);
            Assert.DoesNotContain("\r", File.ReadAllText(path));
            Assert.EndsWith("\n", File.ReadAllText(path));
            Assert.True(BackendMapWriter.Matches(value, path));
            File.WriteAllText(path, File.ReadAllText(path).Replace("Test.cs", "Other.cs"));
            Assert.False(BackendMapWriter.Matches(value, path));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void DebtBaselineDoesNotAbsorbAnAddedOccurrence()
    {
        var baseline = Violation(occurrence: 0);
        var current = new[] { baseline, Violation(occurrence: 1) };

        var comparison = DebtBaseline.Compare(current, new[] { baseline });

        Assert.Single(comparison.Retained);
        Assert.Single(comparison.Added);
        Assert.Empty(comparison.Removed);
        Assert.False(comparison.IsAllowed);
    }

    [Fact]
    public void TarjanFindsOnlyTheStronglyConnectedModuleComponent()
    {
        var edges = new[]
        {
            Edge("Settings", "Plugins"),
            Edge("Plugins", "Settings"),
            Edge("Queues", "Scheduling"),
            Edge("Scheduling", "Scripts"),
        };

        var components = BoundaryRules.FindStronglyConnectedComponents(edges);

        Assert.Contains(components, component => component.SetEquals(new[] { "Settings", "Plugins" }));
        Assert.DoesNotContain(components, component => component.Contains("Queues") && component.Contains("Scheduling"));
    }

    [Fact]
    public void TarjanDoesNotMergeProductionAndTestHostModes()
    {
        var edges = new[]
        {
            Edge("Settings", "Plugins", mode: "production"),
            Edge("Plugins", "Settings", mode: "test-host"),
        };

        var components = BoundaryRules.FindStronglyConnectedComponents(edges);

        Assert.DoesNotContain(components, component => component.SetEquals(new[] { "Settings", "Plugins" }));
    }

    [Fact]
    public void StableMethodIdentityIncludesContainingTypeAndParameterTypes()
    {
        var tree = CSharpSyntaxTree.ParseText("namespace Demo; class Sample { public void Run(int value) { } }");
        var compilation = CSharpCompilation.Create(
            "ArchitectureIdentity",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });
        var method = compilation.GetSemanticModel(tree).GetDeclaredSymbol(
            tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>().Single())!;

        var identity = SymbolIndex.StableSymbolId(method);

        Assert.Equal("global::Demo.Sample.Run(int)", identity);
    }

    private static ArchitectureViolation Violation(int occurrence)
        => new(
            "A004",
            "src/Modules/Settings/Settings.cs",
            "global::NexusPipeline.Modules.Settings.Settings",
            "global::NexusPipeline.Host.HostService",
            "HASH",
            occurrence,
            "module boundary violation",
            "src/Modules/Settings/Settings.cs",
            10)
        {
            Mode = "production",
            ProjectKind = "host",
        };

    private static DependencyEdge Edge(string source, string target, string mode = "production")
        => new("src/test.cs", source, "source", "target", target, "test", 1)
        {
            Mode = mode,
            ProjectKind = "host",
        };
}
