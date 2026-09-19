using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NexusPipeline.Architecture;
using Xunit;

namespace NexusPipeline.Architecture.Tests;

public sealed class ArchitectureRulesTests
{
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

    private static DependencyEdge Edge(string source, string target)
        => new("src/test.cs", source, "source", "target", target, "test", 1)
        {
            Mode = "production",
            ProjectKind = "host",
        };
}
