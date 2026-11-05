using System.Text.Json;
using NexusPipeline.Core.Automation;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record ExecutionFrozenLaunch(string LaunchKind, ExecutionPlanInterpreterImage Source,
    ExecutionPlanInterpreterImage Command, IReadOnlyList<string> SourceArguments, IReadOnlyList<string> Arguments,
    string WorkingDirectory, FileTreePhysicalIdentity WorkingDirectoryIdentity);

internal static class ExecutionLaunchInputs
{
    internal static ExecutionFrozenLaunch Capture(AutomationPath executable, IReadOnlyList<string> arguments,
        string workingDirectory, FileTreePhysicalIdentity workingDirectoryIdentity, IReadOnlyDictionary<Guid, AutomationRoot> roots)
    {
        var sourcePath = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(roots[executable.RootId].Path, executable.RelativePath));
        var kind = Kind(sourcePath);
        var source = ExecutionHookImages.Capture(sourcePath, kind == "native" || Path.GetExtension(sourcePath).Equals(".com", StringComparison.OrdinalIgnoreCase));
        var command = kind == "native" ? source : ExecutionHookImages.Capture(CommandPath(kind), runtime: true);
        var value = new ExecutionFrozenLaunch(kind, source, command, arguments.ToArray(), Arguments(kind, sourcePath, arguments),
            workingDirectory, workingDirectoryIdentity);
        RequireLength(value);
        return value;
    }

    internal static void Require(ExecutionFrozenLaunch launch, string source, IReadOnlyList<string> arguments,
        string workingDirectory, FileTreePhysicalIdentity workingDirectoryIdentity)
    {
        var kind = Kind(source);
        if (launch.SourceArguments.Any(argument => argument.Contains('\0')) || launch.Arguments.Any(argument => argument.Contains('\0')) ||
            launch.Source.Path != source || launch.LaunchKind != kind || !launch.SourceArguments.SequenceEqual(arguments) ||
            !launch.Arguments.SequenceEqual(Arguments(kind, source, arguments)) || launch.WorkingDirectory != workingDirectory ||
            launch.WorkingDirectoryIdentity != workingDirectoryIdentity)
            throw new IOException("execution_launch_source_unbound");
        if (kind == "native")
        {
            if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(launch.Source), JsonSerializer.SerializeToElement(launch.Command)))
                throw new IOException("execution_launch_command_unbound");
        }
        else if (launch.Command.Path != CommandPath(kind) || !Path.GetExtension(source).Equals(".com", StringComparison.OrdinalIgnoreCase) && launch.Source.RuntimeFiles.Count != 0)
            throw new IOException("execution_launch_interpreter_unbound");
        RequireLength(launch);
        foreach (var image in new[] { launch.Source, launch.Command }) RequireImage(image);
    }

    internal static void RequireImage(ExecutionPlanInterpreterImage image)
    {
        if (PhysicalPathPolicy.NormalizeSyntax(image.Path) != image.Path) throw new IOException("execution_launch_path_not_canonical");
        string? prior = null;
        foreach (var file in image.RuntimeFiles)
        {
            PhysicalPathPolicy.ValidateRelativePath(file.RelativePath);
            if (prior is not null && StringComparer.Ordinal.Compare(prior, file.RelativePath) >= 0 ||
                file.RelativePath.Equals(Path.GetFileName(image.Path), StringComparison.OrdinalIgnoreCase))
                throw new IOException("execution_launch_runtime_assets_invalid");
            prior = file.RelativePath;
        }
    }

    internal static string Resolve(AutomationPathsPortablePath path, IReadOnlyDictionary<string, ExecutionPlanRootBindingsItem> roots)
    {
        if (!roots.TryGetValue(path.RootId, out var root)) throw new IOException("execution_launch_root_unbound");
        PhysicalPathPolicy.ValidateRelativePath(path.Relative);
        return PhysicalPathPolicy.NormalizeSyntax(Path.Combine(root.Path, path.Relative));
    }

    private static string Kind(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".exe" => "native", ".bat" or ".cmd" or ".com" => "command", ".ps1" => "powershell",
        _ => throw new IOException("execution_launch_extension_unsupported")
    };

    private static string CommandPath(string kind) => Path.GetFullPath(Path.Combine(Environment.SystemDirectory,
        kind == "command" ? "cmd.exe" : "WindowsPowerShell/v1.0/powershell.exe"));

    private static void RequireLength(ExecutionFrozenLaunch launch)
    {
        var line = WindowsCommandLine.Build(launch.Command.Path, launch.Arguments, launch.LaunchKind == "command"
            ? CommandLineSyntax.CommandInterpreter : CommandLineSyntax.ArgumentVector);
        if (line.Length >= 32767) throw new IOException("execution_launch_command_line_limit");
    }

    private static IReadOnlyList<string> Arguments(string kind, string source, IReadOnlyList<string> arguments)
    {
        if (kind == "native") return arguments.ToArray();
        if (kind == "powershell")
        {
            if (arguments.Any(value => value.Length == 0 || value.Contains('"') || value.Any(char.IsControl)))
                throw new IOException("execution_launch_powershell_argument_unsupported");
            return new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-File", source }.Concat(arguments).ToArray();
        }
        var values = new[] { source }.Concat(arguments).ToArray();
        if (values.Any(value => value.IndexOfAny(['"', '%']) >= 0 || value.Any(char.IsControl)))
            throw new IOException("execution_launch_command_argument_unsupported");
        var nativeChild = Path.GetExtension(source).Equals(".com", StringComparison.OrdinalIgnoreCase);
        var command = string.Join(" ", values.Select(value => nativeChild ? WindowsCommandLine.Quote(value) : "\"" + value + "\""));
        return ["/d", "/s", "/v:off", "/c", "\"" + command + "\""];
    }
}
