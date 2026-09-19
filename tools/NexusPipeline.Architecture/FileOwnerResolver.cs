using System.Text.Json;

namespace NexusPipeline.Architecture;

public sealed class FileOwnerResolver
{
    private readonly Dictionary<string, string> _owners = new(StringComparer.OrdinalIgnoreCase);

    public FileOwnerResolver(string root, string? filePlanPath)
    {
        if (!string.IsNullOrWhiteSpace(filePlanPath) && File.Exists(filePlanPath))
        {
            LoadPlan(filePlanPath);
        }

        _root = Path.GetFullPath(root);
    }

    private readonly string _root;

    public string Resolve(string path)
    {
        var relative = Normalize(path);
        if (_owners.TryGetValue(relative, out var owner))
        {
            return owner;
        }

        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2 && string.Equals(segments[0], "src", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(segments[1], "Host", StringComparison.OrdinalIgnoreCase)) return "Host";
            if (string.Equals(segments[1], "ControlPlane", StringComparison.OrdinalIgnoreCase)) return "ControlPlane";
            if (string.Equals(segments[1], "Platform", StringComparison.OrdinalIgnoreCase)) return "Platform";
            if (string.Equals(segments[1], "Shared", StringComparison.OrdinalIgnoreCase)) return "Shared";
            if (string.Equals(segments[1], "Modules", StringComparison.OrdinalIgnoreCase) && segments.Length >= 3) return segments[2];
            if (string.Equals(segments[1], "NexusPipeline.Plugin.Abstractions", StringComparison.OrdinalIgnoreCase)) return "PluginSdk";
        }

        return "Unassigned";
    }

    public string Normalize(string path)
    {
        var absolute = Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(_root, path));
        var relative = Path.GetRelativePath(_root, absolute);
        return relative.Replace('\\', '/');
    }

    private void LoadPlan(string filePlanPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(filePlanPath));
        if (!document.RootElement.TryGetProperty("records", out var records)) return;
        foreach (var record in records.EnumerateArray())
        {
            if (!record.TryGetProperty("outputs", out var outputs)) continue;
            foreach (var output in outputs.EnumerateArray())
            {
                if (!output.TryGetProperty("owner", out var ownerElement)) continue;
                var owner = ownerElement.GetString();
                if (string.IsNullOrWhiteSpace(owner)) continue;
                foreach (var key in new[] { "mechanicalPath", "finalPath" })
                {
                    if (output.TryGetProperty(key, out var pathElement))
                    {
                        var value = pathElement.GetString();
                        if (!string.IsNullOrWhiteSpace(value)) _owners[value.Replace('\\', '/')] = owner;
                    }
                }
            }
        }
    }
}
