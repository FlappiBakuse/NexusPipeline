using System.Text.Json.Nodes;

// Shared synthetic bytes exercise Host integrity mechanics, not package provenance.
internal static class TaskFixtureResources
{
    internal static List<JsonNode> Read(JsonNode fixture, string fixtures)
    {
        var fixtureResources = fixture["resources"]!.AsArray().Select(r => r!).ToList();
        if (fixture["resourceSets"] is JsonArray resourceSets)
            foreach (var set in resourceSets)
            {
                string relative = set!.GetValue<string>();
                string resourcePath = Path.GetFullPath(Path.Combine(fixtures, relative));
                string allowed = Path.GetFullPath(Path.Combine(fixtures, "resources")) + Path.DirectorySeparatorChar;
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
