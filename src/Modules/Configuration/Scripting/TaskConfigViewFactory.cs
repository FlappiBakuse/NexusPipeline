using NexusPipeline.Modules.Configuration.Paths;
using NexusPipeline.Modules.Plugins.Contracts;

namespace NexusPipeline.Modules.Configuration.Scripting;

internal static class TaskConfigViewFactory
{
    internal static TaskConfigView Capture(string configPath, string rootPath, IReadOnlyList<string> extras, TaskReadResource[] resources)
    {
        var view = new TaskConfigView();
        static string Format(string path) => Path.GetExtension(path).ToLowerInvariant() switch
        { ".json" => "json", ".yaml" or ".yml" => "yaml", _ => "text" };
        void AddDirectory(string directory, string prefix, int depth)
        {
            if (depth > 8) throw new InvalidDataException("resource_limit: config directory depth");
            TaskConfigView.ValidatePath(directory);
            foreach (string path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                TaskConfigView.ValidatePath(path);
                if (Directory.Exists(path)) AddDirectory(path, prefix + Path.GetFileName(path) + "/", depth + 1);
                else if (Format(path) != "text") view.AddConfig(prefix + Path.GetFileName(path), path, Format(path));
            }
        }
        if (File.Exists(configPath)) view.AddConfig("config:" + Path.GetFileName(configPath), configPath, Format(configPath));
        else if (Directory.Exists(configPath)) AddDirectory(configPath, "config:", 0);
        else throw new InvalidDataException("config_unavailable: main configuration missing");
        foreach (var resource in resources)
        {
            string? path;
            if (resource.Source == "root") path = ConfigPathSafety.ResolveWithin(rootPath, resource.Path);
            else
            {
                string[] parts = resource.Path.Split('/', 2);
                if (!int.TryParse(parts[0], out int index) || index < 0 || index >= extras.Count)
                    throw new InvalidDataException("config_unavailable: extraConfig declaration index");
                path = parts.Length == 1 ? extras[index] : ConfigPathSafety.ResolveWithin(extras[index], parts[1]);
            }
            if (path is null) throw new InvalidDataException("config_unavailable: resource outside declared root");
            if (!File.Exists(path) && !resource.Required) continue;
            view.AddResource(resource.Id, path, resource.Format);
        }
        return view;
    }
}
