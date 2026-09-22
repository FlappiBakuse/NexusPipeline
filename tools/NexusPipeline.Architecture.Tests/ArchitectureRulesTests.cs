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
    public void BackendMapValidationRequiresRealSourcesOwnersAndExpectedModes()
    {
        var root = Path.Combine(Path.GetTempPath(), "nxp-map-validation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        Directory.CreateDirectory(Path.Combine(root, "tests"));
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        try
        {
            File.WriteAllText(Path.Combine(root, "src", "Test.cs"), "namespace NexusPipeline.Host; public sealed class Test { }");
            File.WriteAllText(Path.Combine(root, "host.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(root, "tests", "registry.mjs"), "export const registry = {};\n");
            File.WriteAllText(Path.Combine(root, "tests", "sample.test.mjs"), "\n");
            File.WriteAllText(Path.Combine(root, "docs", "map.json"), "{}\n");

            var valid = new
            {
                schemaVersion = 2,
                modes = new[]
                {
                    new { name = "production", projects = new[] { new { kind = "host", path = "host.csproj", sourceFiles = new[] { "src/Test.cs" } } } },
                    new { name = "test-host", projects = new[] { new { kind = "host", path = "host.csproj", sourceFiles = new[] { "src/Test.cs" } } } },
                },
                modules = new[] { new { name = "Host", files = new[] { "src/Test.cs" }, dependsOn = Array.Empty<string>(), entryPoints = Array.Empty<object>() } },
                files = new[] { new { path = "src/Test.cs", owner = "Host", modes = Array.Empty<object>(), declarations = Array.Empty<object>() } },
                entryPoints = Array.Empty<object>(),
                httpRoutes = Array.Empty<object>(),
                mcpTools = Array.Empty<object>(),
                cliEntrypoints = Array.Empty<object>(),
                tests = new { registry = "tests/registry.mjs", files = new[] { "tests/sample.test.mjs" } },
                docs = new[] { new { id = "map", path = "map.json" } },
            };

            Assert.Empty(BackendMapWriter.Validate(valid, root, "both"));

            var invalid = new
            {
                schemaVersion = 2,
                modes = Array.Empty<object>(),
                modules = Array.Empty<object>(),
                files = Array.Empty<object>(),
                entryPoints = Array.Empty<object>(),
                httpRoutes = Array.Empty<object>(),
                mcpTools = Array.Empty<object>(),
                cliEntrypoints = Array.Empty<object>(),
                tests = new { registry = "C:/outside/registry.mjs", files = Array.Empty<string>() },
                docs = Array.Empty<object>(),
            };
            var errors = BackendMapWriter.Validate(invalid, root, "both");
            Assert.Contains(errors, error => error.Contains("files must contain at least one source file", StringComparison.Ordinal));
            Assert.Contains(errors, error => error.Contains("production", StringComparison.Ordinal));
            Assert.Contains(errors, error => error.Contains("safe repository-relative path", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BoundaryGateRejectsControlledModuleToHostDependency()
    {
        var root = Path.Combine(Path.GetTempPath(), "nxp-boundary-negative-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src", "Modules", "Settings"));
        try
        {
            const string relativePath = "src/Modules/Settings/Bad.cs";
            var absolutePath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            File.WriteAllText(absolutePath, "namespace NexusPipeline.Modules.Settings; public sealed class Bad { }");
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(absolutePath), path: relativePath);
            var compilation = CSharpCompilation.Create(
                "ArchitectureBoundaryNegative",
                new[] { tree },
                new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });
            var model = new SyntaxModelFact(tree, compilation.GetSemanticModel(tree), relativePath, "Settings");
            var declaration = new DeclarationFact(
                relativePath,
                "Settings",
                "global::NexusPipeline.Modules.Settings.Bad",
                "Bad",
                "class",
                1,
                false,
                false);
            var edge = new DependencyEdge(
                relativePath,
                "Settings",
                "global::NexusPipeline.Modules.Settings.Bad",
                "global::NexusPipeline.Host.HostService",
                "Host",
                "IdentifierName",
                1);

            var violations = BoundaryRules.Evaluate(
                new[] { model },
                new[] { declaration },
                new[] { edge },
                new FileOwnerResolver(root, null));

            Assert.Contains(violations, violation => violation.RuleId == "A004");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
