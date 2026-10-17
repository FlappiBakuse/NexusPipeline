using System.Text.Json;
using System.Security.Cryptography;
using NexusPipeline.Modules.Plugins.Runtime;
using NexusPipeline.Modules.Plugins.Managed;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Shared.Logging;
using NexusPipeline.Shared.Serialization;

namespace NexusPipeline.Modules.Plugins.Repository;

internal static partial class PluginInstallRecovery
{
    /// <summary>仅首次服务启动且没有任何实例数据时，对四项发行载荷中的两个插件按嵌入文件清单确权。</summary>
    internal static bool SeedBundledForNewInstall(
        bool freshInstance,
        string? pluginsDir = null,
        string? ownershipPath = null,
        string? pendingPath = null,
        string? manifestJson = null)
    {
        if (!freshInstance) return false;
        string pluginRoot = Path.GetFullPath(pluginsDir ?? AppPaths.PluginsDir);
        string ownerFile = Path.GetFullPath(ownershipPath ?? AppPaths.PluginOwnershipPath);
        string pendingFile = Path.GetFullPath(pendingPath ?? AppPaths.PluginPendingPath);
        lock (Sync)
        {
            if (File.Exists(ownerFile) || File.Exists(pendingFile) || !Directory.Exists(pluginRoot)) return false;
            try
            {
                if (manifestJson is null)
                {
                    var assembly = typeof(PluginInstallRecovery).Assembly;
                    string resource = assembly.GetManifestResourceNames().Single(name =>
                        name.EndsWith(".BundledPlugins.json", StringComparison.Ordinal));
                    using Stream stream = assembly.GetManifestResourceStream(resource)!;
                    using var reader = new StreamReader(stream);
                    manifestJson = reader.ReadToEnd();
                }
                BundledPluginManifest manifest = JsonSerializer.Deserialize<BundledPluginManifest>(manifestJson, JsonOpts.Default)
                    ?? throw new InvalidDataException("预装插件清单为空");
                if (manifest.SchemaVersion != 1 || manifest.Repository != "FlappiBakuse/NexusPipeline-Plugins"
                    || manifest.Plugins.Count != 2
                    || !manifest.Plugins.Select(item => item.ArtifactName).ToHashSet(StringComparer.Ordinal).SetEquals(
                        new[] { "EmulatorSupport", "LiveScreenshot" }))
                {
                    throw new InvalidDataException("预装插件身份清单无效");
                }
                string[] roots = Directory.GetFileSystemEntries(pluginRoot);
                if (roots.Length != 2 || roots.Any(path => !manifest.Plugins.Any(item =>
                    string.Equals(item.ArtifactName, Path.GetFileName(path), StringComparison.Ordinal))))
                    return false;
                foreach (BundledPlugin item in manifest.Plugins)
                {
                    if (!PluginRepositoryCatalog.IsCanonicalPluginId(item.Name)
                        || !PluginRepositoryCatalog.IsSafeArtifactName(item.ArtifactName)
                        || item.Files.Count == 0
                        || item.PackageSha256.Length != 64
                        || item.PackageSha256.Any(ch => !char.IsAsciiHexDigit(ch)))
                        return false;
                    string dir = Path.Combine(pluginRoot, item.ArtifactName);
                    if (!Directory.Exists(dir) || (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                        return false;
                    var actual = new Dictionary<string, string>(StringComparer.Ordinal);
                    if (!CollectRegularFiles(dir, dir, actual)) return false;
                    if (actual.Count != item.Files.Count) return false;
                    foreach (BundledPluginFile file in item.Files)
                    {
                        if (!actual.TryGetValue(file.Path, out string? path)
                            || file.Path.Contains('\\') || file.Path.Split('/').Any(part => part is "" or "." or "..")
                            || new FileInfo(path).Length != file.SizeBytes
                            || !string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                                file.Sha256, StringComparison.OrdinalIgnoreCase))
                            return false;
                    }
                    if (!PluginManifest.TryLoad(dir, out PluginManifest? loaded, out _)
                        || loaded is null
                        || loaded.Name != item.Name
                        || loaded.ArtifactName != item.ArtifactName
                        || loaded.Version != item.Version
                        || loaded.Kind != item.Kind
                        || loaded.ApiVersion != item.ApiVersion
                        || loaded.MinHostVersion != item.MinHostVersion)
                        return false;
                }
                var state = new PluginOwnershipState
                {
                    Plugins = manifest.Plugins.Select(item => new PluginOwnership
                    {
                        Name = item.Name,
                        ArtifactName = item.ArtifactName,
                        Version = item.Version,
                        Kind = item.Kind,
                        ApiVersion = item.ApiVersion,
                        Sha256 = item.PackageSha256,
                        Channel = "stable",
                        InstalledAt = DateTimeOffset.UtcNow,
                    }).ToList(),
                };
                SaveOwnership(ownerFile, state);
                PluginInstalledInventory.Invalidate(pluginRoot);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[插件] 预装确权跳过，原文件保持不变：{ex.Message}");
                return false;
            }
        }
    }

    private static bool CollectRegularFiles(string root, string current, Dictionary<string, string> files)
    {
        foreach (string path in Directory.GetFileSystemEntries(current))
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if (!CollectRegularFiles(root, path, files)) return false;
            }
            else
            {
                string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (!files.TryAdd(relative, path)) return false;
            }
        }
        return true;
    }

    private sealed class BundledPluginManifest
    {
        public int SchemaVersion { get; set; }
        public string Repository { get; set; } = "";
        public List<BundledPlugin> Plugins { get; set; } = new();
    }

    private sealed class BundledPlugin
    {
        public string Name { get; set; } = "";
        public string ArtifactName { get; set; } = "";
        public string Version { get; set; } = "";
        public string Kind { get; set; } = "";
        public string ApiVersion { get; set; } = "";
        public string MinHostVersion { get; set; } = "";
        public string PackageSha256 { get; set; } = "";
        public List<BundledPluginFile> Files { get; set; } = new();
    }

    private sealed class BundledPluginFile
    {
        public string Path { get; set; } = "";
        public long SizeBytes { get; set; }
        public string Sha256 { get; set; } = "";
    }
}
