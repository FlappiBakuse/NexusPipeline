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

    public static IReadOnlyList<string> Validate(object value, string root, string mode)
    {
        using var document = JsonDocument.Parse(Serialize(value));
        var errors = new List<string>();
        var map = document.RootElement;
        if (map.ValueKind != JsonValueKind.Object)
        {
            return new[] { "map root must be a JSON object" };
        }

        if (!map.TryGetProperty("schemaVersion", out var schemaVersion)
            || schemaVersion.ValueKind != JsonValueKind.Number
            || !schemaVersion.TryGetInt32(out var version)
            || version != 2)
        {
            errors.Add("schemaVersion must be 2");
        }

        var files = RequireArray(map, "files", errors);
        if (files.HasValue && files.Value.GetArrayLength() == 0) errors.Add("files must contain at least one source file");
        var sourcePaths = new HashSet<string>(StringComparer.Ordinal);
        if (files.HasValue)
        {
            var values = files.Value;
            for (var index = 0; index < values.GetArrayLength(); index++)
            {
                var item = values[index];
                var path = RequiredString(item, "path", $"files[{index}]", errors);
                ValidateRepositoryFile(path, root, $"files[{index}].path", errors);
                if (path is not null && !sourcePaths.Add(path.Replace('\\', '/')))
                {
                    errors.Add($"files[{index}].path is duplicated: {path}");
                }

                var owner = RequiredString(item, "owner", $"files[{index}]", errors);
                if (owner is "Unassigned" or "") errors.Add($"files[{index}].owner must resolve to a concrete owner");
            }
        }

        var modes = RequireArray(map, "modes", errors);
        var expectedModes = mode.Equals("both", StringComparison.OrdinalIgnoreCase)
            ? new[] { "production", "test-host" }
            : new[] { mode };
        var actualModes = new HashSet<string>(StringComparer.Ordinal);
        if (modes.HasValue)
        {
            var values = modes.Value;
            for (var index = 0; index < values.GetArrayLength(); index++)
            {
                var item = values[index];
                var name = RequiredString(item, "name", $"modes[{index}]", errors);
                if (name is not null) actualModes.Add(name);
                var projects = RequireArray(item, "projects", errors, $"modes[{index}]");
                if (projects.HasValue && projects.Value.GetArrayLength() == 0)
                {
                    errors.Add($"modes[{index}].projects must not be empty");
                }
                if (!projects.HasValue) continue;
                var projectValues = projects.Value;
                for (var projectIndex = 0; projectIndex < projectValues.GetArrayLength(); projectIndex++)
                {
                    var project = projectValues[projectIndex];
                    var projectPath = RequiredString(project, "path", $"modes[{index}].projects[{projectIndex}]", errors);
                    ValidateRepositoryFile(projectPath, root, $"modes[{index}].projects[{projectIndex}].path", errors);
                    var sourceFiles = RequireArray(project, "sourceFiles", errors, $"modes[{index}].projects[{projectIndex}]");
                    if (sourceFiles.HasValue && sourceFiles.Value.GetArrayLength() == 0)
                    {
                        errors.Add($"modes[{index}].projects[{projectIndex}].sourceFiles must not be empty");
                    }
                    if (!sourceFiles.HasValue) continue;
                    var sourceValues = sourceFiles.Value;
                    for (var sourceIndex = 0; sourceIndex < sourceValues.GetArrayLength(); sourceIndex++)
                    {
                        var sourcePath = sourceValues[sourceIndex].ValueKind == JsonValueKind.String
                            ? sourceValues[sourceIndex].GetString()
                            : null;
                        ValidateRepositoryFile(sourcePath, root, $"modes[{index}].projects[{projectIndex}].sourceFiles[{sourceIndex}]", errors);
                    }
                }
            }
        }

        foreach (var expected in expectedModes)
        {
            if (!actualModes.Contains(expected)) errors.Add($"modes is missing the expected mode: {expected}");
        }
        foreach (var actual in actualModes)
        {
            if (!expectedModes.Contains(actual, StringComparer.Ordinal)) errors.Add($"modes contains an unexpected mode: {actual}");
        }

        var modules = RequireArray(map, "modules", errors);
        if (modules.HasValue && modules.Value.GetArrayLength() == 0) errors.Add("modules must contain at least one owner");
        if (modules.HasValue)
        {
            var values = modules.Value;
            for (var index = 0; index < values.GetArrayLength(); index++)
            {
                var module = values[index];
                RequiredString(module, "name", $"modules[{index}]", errors);
                var moduleFiles = RequireArray(module, "files", errors, $"modules[{index}]");
                if (moduleFiles.HasValue && moduleFiles.Value.GetArrayLength() == 0)
                {
                    errors.Add($"modules[{index}].files must not be empty");
                }
                if (!moduleFiles.HasValue) continue;
                var fileValues = moduleFiles.Value;
                for (var fileIndex = 0; fileIndex < fileValues.GetArrayLength(); fileIndex++)
                {
                    var modulePath = fileValues[fileIndex].ValueKind == JsonValueKind.String
                        ? fileValues[fileIndex].GetString()
                        : null;
                    ValidateRepositoryFile(modulePath, root, $"modules[{index}].files[{fileIndex}]", errors);
                    if (modulePath is not null && !sourcePaths.Contains(modulePath.Replace('\\', '/')))
                    {
                        errors.Add($"modules[{index}].files[{fileIndex}] is not present in files: {modulePath}");
                    }
                }
            }
        }

        var tests = RequiredObject(map, "tests", errors);
        if (tests.HasValue)
        {
            var registry = RequiredString(tests.Value, "registry", "tests", errors);
            ValidateRepositoryFile(registry, root, "tests.registry", errors);
            var testFiles = RequireArray(tests.Value, "files", errors, "tests");
            if (testFiles.HasValue && testFiles.Value.GetArrayLength() == 0) errors.Add("tests.files must not be empty");
            if (testFiles.HasValue)
            {
                var values = testFiles.Value;
                for (var index = 0; index < values.GetArrayLength(); index++)
                {
                    var testPath = values[index].ValueKind == JsonValueKind.String ? values[index].GetString() : null;
                    ValidateRepositoryFile(testPath, root, $"tests.files[{index}]", errors);
                }
            }
        }

        var docs = RequireArray(map, "docs", errors);
        if (docs.HasValue && docs.Value.GetArrayLength() == 0) errors.Add("docs must contain the current documentation index");
        if (docs.HasValue)
        {
            var values = docs.Value;
            for (var index = 0; index < values.GetArrayLength(); index++)
            {
                var path = RequiredString(values[index], "path", $"docs[{index}]", errors);
                ValidateRepositoryFile(path is null ? null : $"docs/{path}", root, $"docs[{index}].path", errors);
            }
        }

        foreach (var requiredArray in new[] { "entryPoints", "httpRoutes", "mcpTools", "cliEntrypoints" })
        {
            RequireArray(map, requiredArray, errors);
        }
        return errors;
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
        }).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";

    private static JsonElement? RequireArray(JsonElement parent, string name, ICollection<string> errors, string? context = null)
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"{context ?? "map"}.{name} must be an array");
            return null;
        }
        return value;
    }

    private static JsonElement? RequiredObject(JsonElement parent, string name, ICollection<string> errors)
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"map.{name} must be an object");
            return null;
        }
        return value;
    }

    private static string? RequiredString(JsonElement parent, string name, string context, ICollection<string> errors)
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            errors.Add($"{context}.{name} must be a string");
            return null;
        }
        var result = value.GetString();
        if (string.IsNullOrWhiteSpace(result)) errors.Add($"{context}.{name} must not be empty");
        return result;
    }

    private static void ValidateRepositoryFile(string? path, string root, string field, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var normalized = path.Replace('\\', '/');
        if (!IsSafeRelativePath(normalized))
        {
            errors.Add($"{field} must be a safe repository-relative path: {path}");
            return;
        }
        var absolute = Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(absolute)) errors.Add($"{field} does not exist: {path}");
    }

    private static bool IsSafeRelativePath(string path)
        => !string.IsNullOrWhiteSpace(path)
            && !path.StartsWith("/", StringComparison.Ordinal)
            && !path.StartsWith("//", StringComparison.Ordinal)
            && !path.StartsWith("../", StringComparison.Ordinal)
            && !path.Contains("/../", StringComparison.Ordinal)
            && !path.Equals("..", StringComparison.Ordinal)
            && !(path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && path[2] == '/');

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
