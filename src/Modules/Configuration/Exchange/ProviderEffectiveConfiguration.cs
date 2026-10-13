using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NexusPipeline.Modules.Configuration.Paths;
using NexusPipeline.Modules.Configuration.Recovery;

namespace NexusPipeline.Modules.Configuration.Exchange;

internal static class ProviderEffectiveConfiguration
{
    internal static string PathFor(string script, string user) => Path.Combine(ConfigPaths.WorkDir(script, user),
        "provider-effective", "configuration.json");

    internal static void Prepare(ConfigSessionMark mark, JsonObject configuration)
    {
        string file = PathFor(mark.ScriptId, mark.UserId);
        RequirePath(mark, file);
        string directory = Path.GetDirectoryName(file)!;
        if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("provider configuration residue");
        byte[] bytes = Encoding.UTF8.GetBytes(configuration.ToJsonString());
        mark.ProviderConfigurationHash = Convert.ToHexString(SHA256.HashData(bytes));
        // Persist ownership before creating bytes so an interrupted preparation is recoverable.
        mark.Write();
        Directory.CreateDirectory(directory);
        using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes); stream.Flush(true);
    }

    internal static bool RestoreAndCleanup(ConfigSessionMark mark)
    {
        if (mark.ProviderConfigurationHash is null) return true;
        string file = PathFor(mark.ScriptId, mark.UserId);
        RequirePath(mark, file);
        string backup = ConfigPaths.ReplaceBackupDir(mark.ScriptId, mark.UserId);
        if (Directory.Exists(backup))
        {
            string meta = Path.Combine(backup, ".meta");
            string original = Path.Combine(backup, "configuration.json");
            RequireUnlinked(backup);
            if (!File.Exists(meta) || !File.Exists(original)
                || Directory.EnumerateFileSystemEntries(backup).Any(entry => entry != meta && entry != original)) return false;
            RequireUnlinked(meta); RequireUnlinked(original);
            var manifest = JsonNode.Parse(File.ReadAllText(meta))!.AsObject();
            if (manifest["ConfigPath"]?.GetValue<string>() != file
                || manifest["NewFiles"] is not JsonArray { Count: 0 }) return false;
            using var source = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.None);
            if (Convert.ToHexString(SHA256.HashData(source)) != mark.ProviderConfigurationHash) return false;
        }
        if (!ConfigSwapSession.RestoreConfigReplacements(mark.ScriptId, mark.UserId)) return false;
        string directory = Path.GetDirectoryName(file)!;
        if (File.Exists(file))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
            if (Convert.ToHexString(SHA256.HashData(stream)) != mark.ProviderConfigurationHash) return false;
        }
        if (Directory.Exists(directory))
        {
            if (Directory.EnumerateFileSystemEntries(directory).Any(entry => entry != file)) return false;
            if (File.Exists(file)) File.Delete(file);
            Directory.Delete(directory);
        }
        ConfigWorkAreaService.CleanupScriptArea(mark.ScriptId, mark.UserId);
        return true;
    }

    private static void RequirePath(ConfigSessionMark mark, string file)
    {
        if (!Path.GetFullPath(mark.ConfigPath).Equals(Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase))
            throw new IOException("provider configuration ownership mismatch");
        RequireUnlinked(file);
    }

    private static void RequireUnlinked(string file)
    {
        for (string? current = file; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("provider configuration link");
    }
}
