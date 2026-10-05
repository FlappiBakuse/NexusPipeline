using System.Text.Json.Nodes;

// Shared synthetic bytes exercise Host integrity mechanics, not package provenance.
internal static class TaskFixtures
{
    internal static IReadOnlyDictionary<string, string> Index(string fixtures)
    {
        string root = Path.GetFullPath(fixtures);
        for (DirectoryInfo? parent = new(root); parent is not null; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked fixture root");
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new Stack<string>();
        directories.Push(root);
        while (directories.TryPop(out string? directory))
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Linked fixture entry");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Push(entry);
                    continue;
                }
                string relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
                if (relative.StartsWith("shared/resources/", StringComparison.Ordinal)) continue;
                if (!string.Equals(Path.GetExtension(entry), ".json", StringComparison.Ordinal)) continue;
                if (!relative.Contains('/') || relative.StartsWith("../", StringComparison.Ordinal))
                    throw new InvalidDataException("Fixture must belong to a group");
                string identity = Path.GetFileNameWithoutExtension(entry);
                if (!identities.Add(identity)) throw new InvalidDataException($"Duplicate fixture ID: {identity}");
                index.Add(identity, entry);
            }
        }
        if (index.Count == 0) throw new InvalidDataException("Zero task protocol fixtures");
        return index;
    }

    internal static List<JsonNode> Read(JsonNode fixture, string fixtures)
    {
        var fixtureResources = fixture["resources"]!.AsArray().Select(r => r!).ToList();
        if (fixture["resourceSets"] is JsonArray resourceSets)
            foreach (var set in resourceSets)
            {
                string relative = set!.GetValue<string>();
                if (Path.IsPathRooted(relative) || relative.Split(['/', '\\']).Any(part => part is "." or ".."))
                    throw new InvalidDataException("Fixture resource set path");
                string resourcePath = Path.GetFullPath(Path.Combine(fixtures, relative));
                string allowed = Path.GetFullPath(Path.Combine(fixtures, "shared", "resources")) + Path.DirectorySeparatorChar;
                if (!resourcePath.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || Path.GetExtension(resourcePath) != ".json")
                    throw new InvalidDataException("Fixture resource set path");
                var shared = JsonNode.Parse(File.ReadAllText(resourcePath))!.AsObject();
                if (shared["provenance"]?.GetValue<string>() is not ("synthetic_integrity_mechanics_not_upstream_source" or "pinned_upstream_source_with_synthetic_extras"))
                    throw new InvalidDataException("Fixture resource set provenance");
                foreach (var resource in shared["resources"]!.AsArray())
                    if (!fixtureResources.Any(r => r["id"]!.GetValue<string>() == resource!["id"]!.GetValue<string>()))
                        fixtureResources.Add(resource!);
            }
        return fixtureResources;
    }
}
