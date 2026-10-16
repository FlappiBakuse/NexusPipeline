using System.Text.Json;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Security;
using NexusPipeline.Platform.Storage;
namespace NexusPipeline.Modules.Updates;
internal sealed record MaintenanceSettings(UpdateOptions Update, OutboundProxyOptions Proxy, string Locale)
{
    internal static MaintenanceSettings Read(string root)
    {
        string path = Path.Combine(root, "config", "settings.json");
        PayloadPathSafety.RequireLinkFree(path);
        if (!File.Exists(path)) return new(new("", "prerelease"), OutboundProxyOptions.Direct, "zh-CN");
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new IOException("update.settings_too_large");
        // Host settings use the atomic UTF-8 writer, which emits a BOM.
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var settings = document.RootElement;
        if (settings.ValueKind != JsonValueKind.Object) throw new InvalidDataException("update.settings_invalid");
        string Text(string key, string fallback = "") => settings.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
        if (!SecretStore.TryDecrypt(Text("ProxyPassword"), out string? password)) throw new IOException("update.proxy_credentials_unavailable");
        return new(new(Text("UpdateSourceUrl"), Text("UpdateChannel", "prerelease")),
            new(Text("ProxyMode", "none"), Text("ProxyUrl"), Text("ProxyUsername"), password ?? ""), Text("HostLocale", "zh-CN"));
    }
    internal OutboundHttpClientProvider CreateOutbound()
    {
#if NEXUS_TEST_HOST
        return new(() => Proxy, NexusPipeline.Maintenance.Testing.TestHostTransport.CreateFromEnvironment());
#else
        return new(() => Proxy);
#endif
    }
}
