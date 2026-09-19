using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace NexusPipeline.Architecture;

public sealed record LoadedProject(
    string Root,
    string Mode,
    Project Project,
    IReadOnlyList<WorkspaceDiagnostic> WorkspaceDiagnostics);

public sealed record WorkspaceDiagnostic(string Kind, string Message);

public static class ProjectLoader
{
    public static async Task<LoadedProject> LoadAsync(
        string root,
        string mode,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Workspace root does not exist: {root}");
        }

        if (!string.Equals(mode, "production", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(mode, "test-host", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(mode, "both", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Unsupported architecture mode: {mode}", nameof(mode));
        }

        if (!MSBuildLocator.IsRegistered)
        {
            MSBuildLocator.RegisterDefaults();
        }

        var isolatedObj = Path.Combine("obj", "architecture", mode) + Path.DirectorySeparatorChar;
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["NexusTestHost"] = string.Equals(mode, "test-host", StringComparison.OrdinalIgnoreCase) ? "true" : "false",
            ["BaseIntermediateOutputPath"] = isolatedObj,
            ["MSBuildProjectExtensionsPath"] = isolatedObj,
            ["DesignTimeBuild"] = "true",
            ["GenerateAssemblyInfo"] = "false",
            ["GenerateTargetFrameworkAttribute"] = "false",
        };

        using var workspace = MSBuildWorkspace.Create(properties);
        var diagnostics = new List<WorkspaceDiagnostic>();
        workspace.WorkspaceFailed += (_, args) =>
            diagnostics.Add(new WorkspaceDiagnostic(args.Diagnostic.Kind.ToString(), args.Diagnostic.Message));

        var projectPath = Path.Combine(root, "src", "NexusPipeline.csproj");
        if (!File.Exists(projectPath))
        {
            throw new FileNotFoundException("Host project was not found.", projectPath);
        }

        var project = await workspace.OpenProjectAsync(projectPath, progress: null, cancellationToken).ConfigureAwait(false);
        if (diagnostics.Count > 0)
        {
            throw new InvalidOperationException(
                "MSBuild workspace reported failures: " + string.Join(" | ", diagnostics.Select(d => d.Message)));
        }

        return new LoadedProject(root, mode, project, diagnostics);
    }
}
