using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage.Configuration;

internal static class ConfigurationTargetPaths
{
    internal static void Validate(RuntimeInstance instance, IReadOnlyList<ConfigurationTarget> targets)
    {
        var paths = targets.Select(target => PhysicalPathPolicy.NormalizeWithoutLinks(target.TargetPath)).ToArray();
        if (paths.Where((path, index) => path != targets[index].TargetPath).Any() || paths.Any(path =>
            path == Path.GetPathRoot(path) || Within(instance.RuntimeRoot, path) || Within(path, instance.RuntimeRoot)))
            throw new ConfigurationException("configuration_edit_target_invalid");
        if (paths.Where((path, index) => paths.Where((_, other) => other != index).Any(other => Within(path, other))).Any())
            throw new ConfigurationException("configuration_edit_target_overlap");
        foreach (var path in paths) _ = Canonical(path);
    }

    internal static string Canonical(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var name = PhysicalObjectIdentity.EntryName(path);
        if (!name.Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase))
            throw new ConfigurationException("configuration_edit_target_alias_rejected");
        return Path.Combine(Path.GetDirectoryName(path)!, name);
    }

    private static bool Within(string parent, string child) => string.Equals(parent, child, StringComparison.OrdinalIgnoreCase) ||
        child.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
