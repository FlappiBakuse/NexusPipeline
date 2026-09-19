using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexusPipeline.Architecture;

public static class BackendMapWriter
{
    public static object Build(
        CompilationFacts facts,
        IReadOnlyList<DeclarationFact> declarations,
        IReadOnlyList<DependencyEdge> edges)
    {
        var files = declarations
            .GroupBy(declaration => declaration.FilePath, StringComparer.Ordinal)
            .Select(group => new
            {
                path = group.Key,
                owner = group.First().Owner,
                declarations = group.Select(declaration => new
                {
                    id = declaration.SymbolId,
                    name = declaration.Name,
                    kind = declaration.Kind,
                    line = declaration.Line,
                    nested = declaration.IsNested,
                    partial = declaration.IsPartial,
                }).OrderBy(declaration => declaration.line).ThenBy(declaration => declaration.id, StringComparer.Ordinal).ToArray(),
            })
            .OrderBy(file => file.path, StringComparer.Ordinal)
            .ToArray();
        var modules = edges
            .GroupBy(edge => edge.SourceOwner, StringComparer.Ordinal)
            .Select(group => new
            {
                name = group.Key,
                files = group.Select(edge => edge.SourceFile).Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToArray(),
                dependsOn = group.Select(edge => edge.TargetOwner).Distinct(StringComparer.Ordinal).OrderBy(owner => owner, StringComparer.Ordinal).ToArray(),
                entryPoints = Array.Empty<string>(),
            })
            .OrderBy(module => module.name, StringComparer.Ordinal)
            .ToArray();

        return new
        {
            schemaVersion = 1,
            sourceRevision = facts.Project.Version.ToString(),
            mode = facts.Mode,
            modules,
            files,
            tests = files.Where(file => file.path.StartsWith("tests/", StringComparison.Ordinal)).ToArray(),
        };
    }

    public static void Write(object value, string path)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        var json = JsonSerializer.Serialize(value, options) + Environment.NewLine;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, json);
    }
}
