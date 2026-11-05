using System.Text;
using System.Text.Json;
using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record JudgementInputFile(string Root, string Path, string AbsolutePath);

internal static class JudgementFileInventory
{
    internal static void VerifySaved(ExecutionPlanStep step, JsonElement files)
    {
        if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() > 4096) throw Invalid();
        long bytes = 0; string? previous = null;
        foreach (var file in files.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.Object || file.EnumerateObject().Count() != 3 ||
                !file.TryGetProperty("root", out var rootValue) || !file.TryGetProperty("path", out var pathValue) ||
                !file.TryGetProperty("absolutePath", out var absoluteValue)) throw Invalid();
            var root = rootValue.GetString(); var relative = pathValue.GetString(); var absolute = absoluteValue.GetString();
            if (root is not ("script" or "configuration") || relative is null || absolute is null ||
                PhysicalPathPolicy.NormalizeSyntax(absolute) != absolute) throw Invalid();
            PhysicalPathPolicy.ValidateRelativePath(relative);
            var origins = root == "script" ? new[] { step.WorkingDirectory } : step.ConfigurationTargets;
            if (!origins.Any(origin => absolute == origin && Path.GetFileName(origin) == relative ||
                Path.GetFullPath(Path.Combine(origin, relative)) == absolute)) throw Invalid();
            var key = root + "\0" + relative;
            if (previous is not null && StringComparer.Ordinal.Compare(previous, key) > 0) throw Invalid();
            previous = key;
            bytes += Encoding.UTF8.GetByteCount(absolute) + Encoding.UTF8.GetByteCount(relative) + root.Length;
            if (bytes > 1024 * 1024) throw Invalid();
        }
    }

    internal static IReadOnlyList<JudgementInputFile> Read(ExecutionPlanStep step)
    {
        var result = new List<JudgementInputFile>();
        long bytes = 0; var visited = 0;
        void Visit(string root, string path, string origin, int depth = 0)
        {
            if (++visited > 4096 || depth > 128) throw new IOException("execution_judgement_inventory_limit");
            PhysicalPathPolicy.NormalizeWithoutLinks(path);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (FileNotFoundException) { return; }
            catch (DirectoryNotFoundException) { return; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("execution_judgement_inventory_link");
            if ((attributes & FileAttributes.Directory) != 0)
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(path)) Visit(root, child, origin, depth + 1);
            }
            else
            {
                var relative = path == origin ? System.IO.Path.GetFileName(path) : System.IO.Path.GetRelativePath(origin, path).Replace('\\', '/');
                PhysicalPathPolicy.ValidateRelativePath(relative);
                bytes += Encoding.UTF8.GetByteCount(path) + Encoding.UTF8.GetByteCount(relative) + root.Length;
                if (result.Count >= 4096 || bytes > 1024 * 1024) throw new IOException("execution_judgement_inventory_limit");
                result.Add(new(root, relative, path));
            }
        }
        foreach (var configuration in step.ConfigurationTargets) Visit("configuration", configuration, configuration);
        Visit("script", step.WorkingDirectory, step.WorkingDirectory);
        return result.OrderBy(item => item.Root, StringComparer.Ordinal).ThenBy(item => item.Path, StringComparer.Ordinal).ToArray();
    }

    private static IOException Invalid() => new("execution_judgement_inventory_invalid");
}
