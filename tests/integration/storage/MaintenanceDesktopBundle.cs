using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.StorageTests;

internal static class MaintenanceDesktopBundle
{
    internal static void CopyTo(string target)
    {
        var source = Environment.GetEnvironmentVariable("NEXUS_TEST_DESKTOP_BUNDLE") ?? throw new IOException("fixture_desktop_bundle_required");
        source = PhysicalPathPolicy.NormalizeWithoutLinks(source);
        var manifest = Path.Combine(source, "desktop-bundle.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(manifest));
        var root = document.RootElement;
        if (root.GetProperty("format").GetString() != "nexus.desktop.bundle" || root.GetProperty("version").GetInt32() != 1 ||
            root.GetProperty("status").GetString() != "PASS") throw new IOException("fixture_desktop_bundle_invalid");
        var records = root.GetProperty("files").EnumerateArray().ToArray();
        if (records.Length is < 1 or > 512) throw new IOException("fixture_desktop_bundle_invalid");
        var names = records.Select(item => item.GetProperty("path").GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actual = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(source, path).Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (names.Count != records.Length || !actual.SetEquals(names.Append("desktop-bundle.json"))) throw new IOException("fixture_desktop_bundle_changed");
        Directory.CreateDirectory(target);
        foreach (var record in records)
        {
            var relative = record.GetProperty("path").GetString()!; PhysicalPathPolicy.ValidateRelativePath(relative);
            var input = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(source, relative));
            var output = Path.Combine(target, relative); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using (var held = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (held.Length != record.GetProperty("sizeBytes").GetInt64() ||
                    Convert.ToHexStringLower(SHA256.HashData(held)) != record.GetProperty("sha256").GetString())
                    throw new IOException("fixture_desktop_bundle_changed");
                held.Position = 0;
                using var copied = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                held.CopyTo(copied);
            }
        }
        File.Copy(manifest, Path.Combine(target, "desktop-bundle.json"));
    }
}
