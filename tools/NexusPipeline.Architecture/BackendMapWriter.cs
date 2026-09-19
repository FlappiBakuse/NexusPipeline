using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NexusPipeline.Architecture;

public static class BackendMapWriter
{
    private sealed record EntryPoint(string owner, string kind, string name, string file, int line, string symbol);
    private sealed record DocEntry(string id, string path);

    public static object Build(
        CompilationFacts facts,
        IReadOnlyList<DeclarationFact> declarations,
        IReadOnlyList<DependencyEdge> edges)
        => Build(new[] { facts }, Array.Empty<SyntaxModelFact>(), declarations, edges, facts.Root);

    public static object Build(
        IReadOnlyList<CompilationFacts> facts,
        IReadOnlyList<SyntaxModelFact> models,
        IReadOnlyList<DeclarationFact> declarations,
        IReadOnlyList<DependencyEdge> edges,
        string root)
    {
        var declarationGroups = declarations
            .GroupBy(declaration => declaration.FilePath, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.Line).ThenBy(item => item.SymbolId, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var allSourceFiles = facts
            .SelectMany(fact => fact.SourceFiles.Select(file => (file, fact.Mode, fact.ProjectKind)))
            .GroupBy(item => item.file, StringComparer.Ordinal)
            .Select(group => new
            {
                path = group.Key,
                owner = declarations.FirstOrDefault(declaration => declaration.FilePath == group.Key)?.Owner
                    ?? models.FirstOrDefault(model => model.FilePath == group.Key)?.Owner
                    ?? "Unassigned",
                modes = group.Select(item => new { mode = item.Mode, project = item.ProjectKind })
                    .Distinct()
                    .OrderBy(item => item.mode, StringComparer.Ordinal)
                    .ThenBy(item => item.project, StringComparer.Ordinal)
                    .ToArray(),
                declarations = declarationGroups.GetValueOrDefault(group.Key, Array.Empty<DeclarationFact>())
                    .Select(declaration => new
                    {
                        id = declaration.SymbolId,
                        name = declaration.Name,
                        kind = declaration.Kind,
                        line = declaration.Line,
                        nested = declaration.IsNested,
                        partial = declaration.IsPartial,
                        mode = declaration.Mode,
                        project = declaration.ProjectKind,
                    }).ToArray(),
            })
            .OrderBy(file => file.path, StringComparer.Ordinal)
            .ToArray();

        var owners = allSourceFiles
            .Select(file => file.owner)
            .Where(owner => owner is not ("Unassigned" or "Tests" or "TestFixtures"))
            .Concat(declarations.Select(declaration => declaration.Owner))
            .Where(owner => owner is not ("Unassigned" or "Tests" or "TestFixtures"))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(owner => owner, StringComparer.Ordinal)
            .ToArray();

        var entryPoints = CollectEntryPoints(models);
        var modules = owners.Select(owner => new
        {
            name = owner,
            files = allSourceFiles.Where(file => file.owner == owner).Select(file => file.path).ToArray(),
            dependsOn = edges.Where(edge => edge.SourceOwner == owner && !edge.IsExternal && edge.TargetOwner != owner)
                .Select(edge => edge.TargetOwner)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(target => target, StringComparer.Ordinal)
                .ToArray(),
            entryPoints = entryPoints.Where(entry => entry.owner == owner)
                .Select(entry => new { kind = entry.kind, name = entry.name, file = entry.file, line = entry.line, symbol = entry.symbol })
                .OrderBy(entry => entry.kind, StringComparer.Ordinal)
                .ThenBy(entry => entry.name, StringComparer.Ordinal)
                .ThenBy(entry => entry.file, StringComparer.Ordinal)
                .ThenBy(entry => entry.line)
                .ToArray(),
        }).ToArray();

        var modeMap = facts
            .GroupBy(fact => fact.Mode, StringComparer.Ordinal)
            .Select(group => new
            {
                name = group.Key,
                projects = group.Select(fact => new
                {
                    kind = fact.ProjectKind,
                    path = Path.GetRelativePath(root, fact.ProjectPath).Replace('\\', '/'),
                    sourceFiles = fact.SourceFiles.OrderBy(file => file, StringComparer.Ordinal).ToArray(),
                }).OrderBy(project => project.kind, StringComparer.Ordinal).ToArray(),
            })
            .OrderBy(mode => mode.name, StringComparer.Ordinal)
            .ToArray();

        var httpRoutes = entryPoints.Where(entry => entry.kind == "http")
            .Select(entry => new { name = entry.name, file = entry.file, line = entry.line, symbol = entry.symbol })
            .OrderBy(entry => entry.name, StringComparer.Ordinal)
            .ThenBy(entry => entry.file, StringComparer.Ordinal)
            .ThenBy(entry => entry.line)
            .ToArray();
        var mcpTools = entryPoints.Where(entry => entry.kind == "mcp")
            .Select(entry => new { name = entry.name, file = entry.file, line = entry.line, symbol = entry.symbol })
            .OrderBy(entry => entry.name, StringComparer.Ordinal)
            .ThenBy(entry => entry.file, StringComparer.Ordinal)
            .ThenBy(entry => entry.line)
            .ToArray();
        var cliEntrypoints = entryPoints.Where(entry => entry.kind == "cli")
            .Select(entry => new { name = entry.name, file = entry.file, line = entry.line, symbol = entry.symbol })
            .OrderBy(entry => entry.name, StringComparer.Ordinal)
            .ThenBy(entry => entry.file, StringComparer.Ordinal)
            .ThenBy(entry => entry.line)
            .ToArray();

        return new
        {
            schemaVersion = 2,
            modes = modeMap,
            modules,
            files = allSourceFiles,
            entryPoints = entryPoints
                .Select(entry => new { kind = entry.kind, name = entry.name, file = entry.file, line = entry.line, symbol = entry.symbol })
                .OrderBy(entry => entry.kind, StringComparer.Ordinal)
                .ThenBy(entry => entry.name, StringComparer.Ordinal)
                .ThenBy(entry => entry.file, StringComparer.Ordinal)
                .ThenBy(entry => entry.line)
                .ToArray(),
            httpRoutes,
            mcpTools,
            cliEntrypoints,
            tests = new
            {
                registry = "tests/registry.mjs",
                files = CollectTestFiles(root, facts),
            },
            docs = CollectDocs(root),
        };
    }

    public static bool Matches(object value, string path)
    {
        if (!File.Exists(path)) return false;
        var expected = Serialize(value);
        var actual = File.ReadAllText(path);
        return string.Equals(actual, expected, StringComparison.Ordinal);
    }

    public static void Write(object value, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, Serialize(value));
    }

    private static string Serialize(object value)
        => JsonSerializer.Serialize(value, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        }) + Environment.NewLine;

    private static IReadOnlyList<EntryPoint> CollectEntryPoints(IReadOnlyList<SyntaxModelFact> models)
    {
        var points = new List<EntryPoint>();
        foreach (var model in models.Where(model => model.ProjectKind == "host"))
        {
            var root = model.Tree.GetRoot();
            foreach (var attribute in root.DescendantNodes().OfType<AttributeSyntax>())
            {
                var attributeName = attribute.Name.ToString();
                var kind = attributeName.EndsWith("ApiRoute", StringComparison.Ordinal)
                    || attributeName.EndsWith("ApiRouteAttribute", StringComparison.Ordinal)
                    ? "http"
                    : attributeName.EndsWith("McpServerTool", StringComparison.Ordinal)
                        || attributeName.EndsWith("McpServerToolAttribute", StringComparison.Ordinal)
                        ? "mcp"
                        : null;
                if (kind is null) continue;
                var name = attribute.ArgumentList?.Arguments.FirstOrDefault()?.Expression.ToString().Trim('"') ?? attributeName;
                var declaration = attribute.Parent?.Parent;
                var symbol = declaration is null ? null : SymbolIndex.GetEnclosingSymbol(model, declaration);
                points.Add(new EntryPoint(
                    model.Owner,
                    kind,
                    name,
                    model.FilePath,
                    attribute.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    symbol is null ? $"<file:{model.FilePath}>" : SymbolIndex.StableSymbolId(symbol)));
            }
            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var symbol = model.Model.GetDeclaredSymbol(method);
                if (symbol?.ContainingType?.Name != "CliCommandRouter") continue;
                points.Add(new EntryPoint(
                    model.Owner,
                    "cli",
                    symbol.Name,
                    model.FilePath,
                    method.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    SymbolIndex.StableSymbolId(symbol)));
            }
        }
        return points;
    }

    private static IReadOnlyList<string> CollectTestFiles(string root, IReadOnlyList<CompilationFacts> facts)
    {
        var files = new HashSet<string>(facts
            .Where(fact => fact.ProjectKind.Equals("tests", StringComparison.OrdinalIgnoreCase)
                || fact.ProjectKind.StartsWith("test-", StringComparison.OrdinalIgnoreCase))
            .SelectMany(fact => fact.SourceFiles), StringComparer.Ordinal);
        var testsRoot = Path.Combine(root, "tests");
        if (Directory.Exists(testsRoot))
        {
            foreach (var file in Directory.EnumerateFiles(testsRoot, "*.mjs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                var name = Path.GetFileName(relative);
                if (name.EndsWith(".test.mjs", StringComparison.Ordinal)
                    || name.EndsWith("-smoke.mjs", StringComparison.Ordinal)
                    || relative.StartsWith("tests/documentation/", StringComparison.Ordinal)
                    || relative.StartsWith("tests/e2e/tests/", StringComparison.Ordinal))
                {
                    files.Add(relative);
                }
            }
        }
        files.Add("tests/registry.mjs");
        return files.OrderBy(file => file, StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<DocEntry> CollectDocs(string root)
    {
        var mapPath = Path.Combine(root, "docs", "map.json");
        if (!File.Exists(mapPath)) return Array.Empty<DocEntry>();
        using var document = JsonDocument.Parse(File.ReadAllText(mapPath));
        if (!document.RootElement.TryGetProperty("topics", out var topics)) return Array.Empty<DocEntry>();
        return topics.EnumerateArray()
            .Where(topic => topic.TryGetProperty("id", out _))
            .Select(topic => new DocEntry(
                topic.GetProperty("id").GetString() ?? string.Empty,
                topic.GetProperty("path").GetString() ?? string.Empty))
            .OrderBy(topic => topic.id, StringComparer.Ordinal)
            .ToArray();
    }
}
