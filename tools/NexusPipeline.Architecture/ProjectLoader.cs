using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace NexusPipeline.Architecture;

public sealed record WorkspaceDiagnostic(string Kind, string Message);

public sealed record LoadedProjectPart(
    string Kind,
    string ProjectPath,
    Project Project);

public sealed class LoadedProjectMode : IDisposable
{
    internal LoadedProjectMode(
        string root,
        string mode,
        MSBuildWorkspace workspace,
        IReadOnlyList<LoadedProjectPart> projects,
        IReadOnlyList<WorkspaceDiagnostic> diagnostics)
    {
        Root = root;
        Mode = mode;
        Workspace = workspace;
        Projects = projects;
        WorkspaceDiagnostics = diagnostics;
    }

    public string Root { get; }
    public string Mode { get; }
    public MSBuildWorkspace Workspace { get; }
    public IReadOnlyList<LoadedProjectPart> Projects { get; }
    public IReadOnlyList<WorkspaceDiagnostic> WorkspaceDiagnostics { get; }

    public void Dispose() => Workspace.Dispose();
}

public sealed class LoadedProject : IDisposable
{
    internal LoadedProject(string root, string mode, IReadOnlyList<LoadedProjectMode> modes)
    {
        Root = root;
        Mode = mode;
        Modes = modes;
    }

    public string Root { get; }
    public string Mode { get; }
    public IReadOnlyList<LoadedProjectMode> Modes { get; }

    // Compatibility conveniences for callers that request one mode.
    public Project Project => Modes[0].Projects[0].Project;
    public IReadOnlyList<WorkspaceDiagnostic> WorkspaceDiagnostics => Modes
        .SelectMany(mode => mode.WorkspaceDiagnostics)
        .ToArray();

    public void Dispose()
    {
        foreach (var mode in Modes) mode.Dispose();
    }
}

public static class ProjectLoader
{
    private static readonly (string Kind, string RelativePath)[] HostProjects =
    [
        ("host", "src/NexusPipeline.csproj"),
        ("plugin-sdk", "src/NexusPipeline.Plugin.Abstractions/NexusPipeline.Plugin.Abstractions.csproj"),
    ];

    public static async Task<LoadedProject> LoadAsync(
        string root,
        string mode,
        CancellationToken cancellationToken)
    {
        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException($"Workspace root does not exist: {fullRoot}");
        }

        var requestedModes = mode.Equals("both", StringComparison.OrdinalIgnoreCase)
            ? new[] { "production", "test-host" }
            : new[] { mode };
        if (requestedModes.Any(item => item is not ("production" or "test-host")))
        {
            throw new ArgumentException($"Unsupported architecture mode: {mode}", nameof(mode));
        }

        if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();

        var loadedModes = new List<LoadedProjectMode>();
        try
        {
            foreach (var requestedMode in requestedModes)
            {
                loadedModes.Add(await LoadModeAsync(fullRoot, requestedMode, cancellationToken).ConfigureAwait(false));
            }
            return new LoadedProject(fullRoot, mode, loadedModes);
        }
        catch
        {
            foreach (var loaded in loadedModes) loaded.Dispose();
            throw;
        }
    }

    private static async Task<LoadedProjectMode> LoadModeAsync(
        string root,
        string mode,
        CancellationToken cancellationToken)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["NexusTestHost"] = mode.Equals("test-host", StringComparison.OrdinalIgnoreCase) ? "true" : "false",
            ["NexusArchitectureMode"] = mode,
            ["DesignTimeBuild"] = "true",
            ["GenerateTargetFrameworkAttribute"] = "false",
            ["NuGetAudit"] = "false",
            ["RestoreIgnoreFailedSources"] = "true",
        };

        var workspace = MSBuildWorkspace.Create(properties);
        workspace.LoadMetadataForReferencedProjects = false;
        var diagnostics = new List<WorkspaceDiagnostic>();
        workspace.WorkspaceFailed += (_, args) =>
            diagnostics.Add(new WorkspaceDiagnostic(args.Diagnostic.Kind.ToString(), args.Diagnostic.Message));

        try
        {
            var projectDefinitions = HostProjects
                .AppendIf(mode.Equals("test-host", StringComparison.OrdinalIgnoreCase), ("tests", "tests/NexusPipeline.Tests/NexusPipeline.Tests.csproj"))
                .AppendIf(mode.Equals("test-host", StringComparison.OrdinalIgnoreCase), ("test-fixture", "tests/fixtures/NexusPipeline.TestPlugin/NexusPipeline.TestPlugin.csproj"))
                .ToArray();
            var projects = new List<LoadedProjectPart>();
            foreach (var (kind, relativePath) in projectDefinitions)
            {
                var projectPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
                if (!File.Exists(projectPath)) throw new FileNotFoundException($"Architecture project was not found: {relativePath}", projectPath);
                var project = workspace.CurrentSolution.Projects.FirstOrDefault(candidate =>
                    candidate.FilePath is not null
                    && string.Equals(Path.GetFullPath(candidate.FilePath), projectPath, StringComparison.OrdinalIgnoreCase));
                project ??= await workspace.OpenProjectAsync(projectPath, progress: null, cancellationToken).ConfigureAwait(false);
                ValidateProjectDocuments(root, relativePath, project);
                projects.Add(new LoadedProjectPart(kind, projectPath, project));
            }

            var failures = diagnostics
                .Where(diagnostic => diagnostic.Kind.Equals("Failure", StringComparison.OrdinalIgnoreCase))
                .Where(diagnostic => !IsNonFatalNuGetAuditDiagnostic(diagnostic.Message))
                .ToArray();
            if (failures.Length > 0)
            {
                throw new InvalidOperationException(
                    $"MSBuild workspace reported failures for {mode}: "
                    + string.Join(" | ", failures.Select(diagnostic => diagnostic.Message)));
            }

            return new LoadedProjectMode(root, mode, workspace, projects, diagnostics);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    private static void ValidateProjectDocuments(string root, string projectName, Project project)
    {
        foreach (var document in project.Documents)
        {
            if (string.IsNullOrWhiteSpace(document.FilePath))
            {
                throw new InvalidOperationException($"Project {projectName} contains a document without a source path.");
            }
            var path = Path.GetFullPath(document.FilePath);
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !IsExternalPackageDocument(path))
            {
                throw new InvalidOperationException($"Project {projectName} contains an out-of-root source document: {document.FilePath}");
            }
            if (!File.Exists(path) && !IsExternalPackageDocument(path))
            {
                throw new InvalidOperationException($"Project {projectName} contains a missing source document: {document.FilePath}");
            }
        }
    }

    private static bool IsExternalPackageDocument(string path)
        => path.Replace('\\', '/').Contains("/.nuget/packages/", StringComparison.OrdinalIgnoreCase);

    private static bool IsNonFatalNuGetAuditDiagnostic(string message)
        => message.Contains("获取包漏洞数据时出错", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Unable to load the service index for source", StringComparison.OrdinalIgnoreCase)
                && message.Contains("api.nuget.org", StringComparison.OrdinalIgnoreCase);
}

internal static class EnumerableExtensions
{
    public static IEnumerable<T> AppendIf<T>(this IEnumerable<T> source, bool condition, T value)
        => condition ? source.Append(value) : source;
}
