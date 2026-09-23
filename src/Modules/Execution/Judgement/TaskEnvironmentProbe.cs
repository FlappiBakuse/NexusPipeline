using NexusPipeline.Platform.Storage;
using System.Text.Json.Nodes;
using NexusPipeline.Modules.Configuration.Paths;
using NexusPipeline.Modules.Configuration.Scripting;
using NexusPipeline.Modules.Execution.Targets;
using NexusPipeline.Modules.Plugins.Contracts;

namespace NexusPipeline.Modules.Execution.Judgement;

/// <summary>
/// Host-owned implementation of taskProtocol 1.2 declared target inspection.
/// It resolves only frozen resource selectors or the two host fields declared by
/// the manifest, and returns metadata/status rather than a path or file content.
/// </summary>
internal sealed class TaskEnvironmentProbe
{
    private readonly IReadOnlyDictionary<string, TaskEnvironmentCheckDescriptor> _checks;
    private readonly TaskConfigView _view;
    private readonly TaskExecutionContext _context;
    private readonly string _scriptRoot;
    private readonly string _scriptExecutable;

    internal TaskEnvironmentProbe(
        TaskEnvironmentCheckDescriptor[] checks,
        TaskConfigView view,
        TaskExecutionContext context,
        string scriptRoot,
        string scriptExecutable)
    {
        _checks = checks.ToDictionary(check => check.Id, StringComparer.Ordinal);
        _view = view;
        _context = context;
        _scriptRoot = scriptRoot;
        _scriptExecutable = scriptExecutable;
    }

    internal TaskEnvironmentInspection Inspect(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !_checks.TryGetValue(id, out var check))
            return new(id ?? "", "not_checked", "unsupported", null, null, "undeclared_inspection");

        if (check.SourceKind is "config" or "resource" or "mainConfig")
        {
            string? resourceId = check.ResourceId;
            if (check.SourceKind == "mainConfig")
            {
                var main = _view.ConfigResources;
                if (main.Length != 1) return NotChecked(check, "main_config_not_unique");
                resourceId = main[0].Id;
            }
            if (resourceId is null || check.Selector is null)
                return NotChecked(check, "invalid_declaration");
            if (!_view.TryResolveDeclaredTarget(resourceId, check.Selector, out TaskDeclaredTarget? target, out string sourceStatus, check.DefaultValue))
                return new(check.Id, sourceStatus, check.ExpectedKind, null, null, "config_target_" + sourceStatus);
            if (check.Comparison == "adb_endpoint_with_port")
            {
                if (check.SecondarySelector is null)
                    return NotChecked(check, "invalid_declaration");
                if (!_view.TryResolveDeclaredTarget(resourceId, check.SecondarySelector,
                    out TaskDeclaredTarget? port, out string portStatus, check.SecondaryDefaultValue, allowInteger: true))
                    return new(check.Id, portStatus, check.ExpectedKind, null, null, "config_target_" + portStatus);
                return ProbeAdb(check, target!.Value + ":" + port!.Value);
            }
            return ProbePath(check, target!.Value, target.BaseDirectory);
        }

        if (check.SourceKind != "host" || check.HostField is null)
            return NotChecked(check, "invalid_declaration");
        if (check.HostField == "gameTarget")
        {
            if (_context.GameTarget.Kind == "none" || string.IsNullOrWhiteSpace(_context.GameTarget.Value))
                return new(check.Id, "missing", check.ExpectedKind, null, false, "host_game_target_missing");
            if (check.ExpectedKind == "adb_endpoint")
                return ProbeAdb(check, _context.GameTarget.Value);
            return ProbePath(check, _context.GameTarget.Value, "");
        }
        if (check.HostField == "scriptExecutable")
        {
            if (string.IsNullOrWhiteSpace(_scriptExecutable))
                return new(check.Id, "missing", check.ExpectedKind, null, null, "host_script_target_missing");
            return ProbePath(check, _scriptExecutable, "");
        }
        return NotChecked(check, "unsupported_host_field");
    }

    private TaskEnvironmentInspection ProbePath(TaskEnvironmentCheckDescriptor check, string raw, string configDirectory)
    {
        if (check.ExpectedKind == "adb_endpoint") return ProbeAdb(check, raw);
        if (IsSpecialPath(raw)) return new(check.Id, "unsupported", check.ExpectedKind, null, null, "unsupported_target");

        string? path = ResolvePath(check.RelativeBase, raw, configDirectory);
        if (path is null) return new(check.Id, "not_checked", check.ExpectedKind, null, null, "target_not_resolvable");
        try
        {
            string full = Path.GetFullPath(path);
            if (HasReparsePoint(full)) return new(check.Id, "unsupported", check.ExpectedKind, null, null, "reparse_target");
            FileAttributes attributes;
            try
            {
                attributes = LocalPathMetadata.ReadAttributes(full);
            }
            catch (FileNotFoundException)
            {
                return new(check.Id, "missing", check.ExpectedKind, null, MatchesContext(check, full, null), "target_missing");
            }
            catch (DirectoryNotFoundException)
            {
                return new(check.Id, "missing", check.ExpectedKind, null, MatchesContext(check, full, null), "target_missing");
            }
            catch (UnauthorizedAccessException)
            {
                return new(check.Id, "access_denied", check.ExpectedKind, null, null, "target_access_denied");
            }
            catch (NotSupportedException)
            {
                return new(check.Id, "unsupported", check.ExpectedKind, null, null, "unsupported_target");
            }
            catch (IOException)
            {
                return new(check.Id, "not_checked", check.ExpectedKind, null, null, "target_probe_error");
            }

            string actualKind = (attributes & FileAttributes.Directory) != 0 ? "directory" : "file";
            bool? matchesContext = MatchesContext(check, full, actualKind);
            if (check.ExpectedKind != "file_or_directory"
                && !string.Equals(actualKind, check.ExpectedKind, StringComparison.Ordinal))
                return new(check.Id, "wrong_kind", check.ExpectedKind, actualKind, matchesContext, "target_wrong_kind");
            return new(check.Id, "present", check.ExpectedKind, actualKind, matchesContext, "target_present");
        }
        catch (UnauthorizedAccessException)
        {
            return new(check.Id, "access_denied", check.ExpectedKind, null, null, "target_access_denied");
        }
        catch (NotSupportedException)
        {
            return new(check.Id, "unsupported", check.ExpectedKind, null, null, "unsupported_target");
        }
        catch (IOException)
        {
            return new(check.Id, "not_checked", check.ExpectedKind, null, null, "target_probe_error");
        }
    }

    private TaskEnvironmentInspection ProbeAdb(TaskEnvironmentCheckDescriptor check, string raw)
    {
        bool valid = EmulatorSupport.IsValidAdbAddress(raw);
        return valid
            ? new(check.Id, "present", "adb_endpoint", "adb_endpoint", MatchesContext(check, raw, "adb_endpoint"), "endpoint_format_valid")
            : new(check.Id, "wrong_kind", "adb_endpoint", "unsupported", MatchesContext(check, raw, "adb_endpoint"), "endpoint_format_invalid");
    }

    private TaskEnvironmentInspection NotChecked(TaskEnvironmentCheckDescriptor check, string reason) =>
        new(check.Id, "not_checked", check.ExpectedKind, null, null, reason);

    private string? ResolvePath(string relativeBase, string raw, string configDirectory)
    {
        string value = raw.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
        if (IsSpecialPath(value)) return null;
        if (Path.IsPathFullyQualified(value)) return value;
        if (Path.IsPathRooted(value)) return null;
        string? root = relativeBase switch
        {
            "script_root" => _scriptRoot,
            "config_directory" => configDirectory,
            "none" => null,
            _ => null,
        };
        return root is null ? null : ConfigPathSafety.ResolveWithin(root, value);
    }

    private bool? MatchesContext(TaskEnvironmentCheckDescriptor check, string value, string? actualKind)
    {
        string? context = _context.GameTarget.Value;
        if (string.IsNullOrWhiteSpace(context) || _context.GameTarget.Kind == "none") return null;
        if (check.Comparison == "adb_endpoint_with_port" || _context.GameTarget.Kind == "adb_endpoint")
            return string.Equals(NormalizeEndpoint(value), NormalizeEndpoint(context), StringComparison.OrdinalIgnoreCase);
        try
        {
            if (IsSpecialPath(value) || IsSpecialPath(context)) return false;
            string candidate = Path.GetFullPath(value);
            string expected = Path.GetFullPath(context);
            if (check.Comparison == "path_or_executable_parent")
            {
                string kind = actualKind ?? (Path.GetExtension(candidate).Equals(".exe", StringComparison.OrdinalIgnoreCase) ? "file" : "directory");
                if (kind == "directory" && _context.GameTarget.Kind == "executable")
                    expected = Path.GetDirectoryName(expected) ?? expected;
            }
            return string.Equals(
                candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                expected.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeEndpoint(string value) => value.Trim().TrimEnd('/');

    private static bool IsSpecialPath(string value)
    {
        string trimmed = value.Trim();
        return trimmed.Length == 0
            || trimmed.StartsWith("\\\\", StringComparison.Ordinal)
            || trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("\\\\?\\", StringComparison.Ordinal)
            || trimmed.StartsWith("\\\\.\\", StringComparison.Ordinal)
            || trimmed.StartsWith("\\Device\\", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("\0", StringComparison.Ordinal)
            || trimmed.StartsWith("\\", StringComparison.Ordinal)
            || trimmed.StartsWith("/", StringComparison.Ordinal)
            || System.Text.RegularExpressions.Regex.IsMatch(trimmed, "%[^%]+%")
            || (trimmed.Length > 2 && trimmed.IndexOf(':', 2) >= 0)
            || (trimmed.Length >= 2 && char.IsLetter(trimmed[0]) && trimmed[1] == ':'
                && (trimmed.Length == 2 || (trimmed[2] != '\\' && trimmed[2] != '/')))
            || (trimmed.Contains("://", StringComparison.Ordinal) && !IsWindowsDrivePath(trimmed));
    }

    private static bool IsWindowsDrivePath(string value) => value.Length >= 3
        && char.IsLetter(value[0]) && value[1] == ':' && (value[2] == '\\' || value[2] == '/');

    private static bool HasReparsePoint(string path)
    {
        // Inspect ancestors before touching descendants: probing a leaf first
        // could follow a directory junction (including a remote target).
        var ancestors = new Stack<string>();
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            ancestors.Push(current);
        while (ancestors.TryPop(out string? current))
        {
            try
            {
                if ((LocalPathMetadata.ReadAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return false;
    }
}
