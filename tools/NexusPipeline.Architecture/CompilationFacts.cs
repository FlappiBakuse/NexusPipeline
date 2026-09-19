using Microsoft.CodeAnalysis;

namespace NexusPipeline.Architecture;

public sealed record CompilationFacts(
    string Root,
    string Mode,
    string ProjectKind,
    string ProjectPath,
    Project Project,
    Compilation Compilation,
    IReadOnlyList<string> SourceFiles,
    IReadOnlyList<string> ReferenceAssemblies,
    IReadOnlyList<Diagnostic> Diagnostics);

public static class CompilationFactsBuilder
{
    public static async Task<IReadOnlyList<CompilationFacts>> BuildAsync(
        LoadedProjectMode loaded,
        CancellationToken cancellationToken)
    {
        var facts = new List<CompilationFacts>();
        foreach (var project in loaded.Projects)
        {
            facts.Add(await BuildAsync(project, loaded.Root, loaded.Mode, cancellationToken).ConfigureAwait(false));
        }
        return facts;
    }

    public static async Task<CompilationFacts> BuildAsync(
        LoadedProjectPart loaded,
        string root,
        string mode,
        CancellationToken cancellationToken)
    {
        var compilation = await loaded.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Roslyn did not produce a compilation for {loaded.ProjectPath}.");
        var duplicateTrees = compilation.SyntaxTrees
            .GroupBy(tree => Path.GetFullPath(tree.FilePath), StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => group.Skip(1))
            .ToArray();
        if (duplicateTrees.Length > 0)
        {
            compilation = compilation.RemoveSyntaxTrees(duplicateTrees);
        }
        var diagnostics = compilation.GetDiagnostics(cancellationToken).ToArray();
        var errors = diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length > 0)
        {
            var details = string.Join(Environment.NewLine, errors.Take(80).Select(diagnostic => diagnostic.ToString()));
            throw new InvalidOperationException(
                $"Compilation contains errors for {loaded.Kind}/{mode}; no architecture report was produced."
                + Environment.NewLine + details);
        }

        var sourcePaths = compilation.SyntaxTrees
            .Select(tree => tree.FilePath)
            .Where(file => !string.IsNullOrWhiteSpace(file))
            .Select(file => Path.GetFullPath(file!))
            .ToArray();
        var outsideWorkspace = sourcePaths
            .Where(file => !file.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Where(file => !file.Replace('\\', '/').Contains("/.nuget/packages/", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (outsideWorkspace.Length > 0)
        {
            throw new InvalidOperationException($"Compilation {loaded.Kind}/{mode} contains a source outside the workspace root: {outsideWorkspace[0]}");
        }

        var files = sourcePaths
            .Where(file => file.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
            .Where(file => !file.StartsWith("obj/", StringComparison.OrdinalIgnoreCase)
                && !file.StartsWith("bin/", StringComparison.OrdinalIgnoreCase)
                && !file.StartsWith("src/obj/", StringComparison.OrdinalIgnoreCase)
                && !file.StartsWith("src/bin/", StringComparison.OrdinalIgnoreCase)
                && !file.Contains("/.generated/", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file, StringComparer.Ordinal)
            .ToArray();
        if (files.Any(file => file.StartsWith("../", StringComparison.Ordinal) || file.Equals("..", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Compilation {loaded.Kind}/{mode} contains a source outside the workspace root.");
        }

        var references = compilation.References
            .Select(reference => reference.Display ?? string.Empty)
            .Where(display => display.Length > 0)
            .OrderBy(display => display, StringComparer.Ordinal)
            .ToArray();

        return new CompilationFacts(
            root,
            mode,
            loaded.Kind,
            loaded.ProjectPath,
            loaded.Project,
            compilation,
            files,
            references,
            diagnostics);
    }

    public static async Task<CompilationFacts> BuildAsync(
        LoadedProject loaded,
        CancellationToken cancellationToken)
    {
        var mode = loaded.Modes[0];
        return (await BuildAsync(mode, cancellationToken).ConfigureAwait(false)).First();
    }
}
