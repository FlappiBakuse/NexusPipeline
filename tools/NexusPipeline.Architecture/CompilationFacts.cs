using Microsoft.CodeAnalysis;

namespace NexusPipeline.Architecture;

public sealed record CompilationFacts(
    string Root,
    string Mode,
    Project Project,
    Compilation Compilation,
    IReadOnlyList<string> SourceFiles,
    IReadOnlyList<string> ReferenceAssemblies,
    IReadOnlyList<Diagnostic> Diagnostics);

public static class CompilationFactsBuilder
{
    public static async Task<CompilationFacts> BuildAsync(
        LoadedProject loaded,
        CancellationToken cancellationToken)
    {
        var compilation = await loaded.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn did not produce a compilation.");
        var diagnostics = compilation.GetDiagnostics(cancellationToken).ToArray();
        var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length > 0)
        {
            var details = string.Join(Environment.NewLine, errors.Take(50).Select(d => d.ToString()));
            throw new InvalidOperationException("Compilation contains errors; no architecture report was produced." + Environment.NewLine + details);
        }

        var files = compilation.SyntaxTrees
            .Select(tree => Path.GetRelativePath(loaded.Root, tree.FilePath).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var references = compilation.References
            .Select(reference => reference.Display ?? string.Empty)
            .Where(display => display.Length > 0)
            .OrderBy(display => display, StringComparer.Ordinal)
            .ToArray();

        return new CompilationFacts(loaded.Root, loaded.Mode, loaded.Project, compilation, files, references, diagnostics);
    }
}
