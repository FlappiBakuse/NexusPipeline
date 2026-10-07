using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NexusPipeline.Modules.Plugins.Contracts;

internal sealed record PluginScriptTypeIcon(Uri Uri, string Sha256, string ContentType)
{
    internal static bool TryParse(JsonNode? node, out PluginScriptTypeIcon? source)
    {
        source = null;
        if (node is not JsonObject data
            || data["url"] is not JsonValue urlValue || !urlValue.TryGetValue(out string? url)
            || data["sha256"] is not JsonValue hashValue || !hashValue.TryGetValue(out string? hash)
            || data["contentType"] is not JsonValue mimeValue || !mimeValue.TryGetValue(out string? mime)
            || url is null || url.Length > 2048 || hash is null || !Regex.IsMatch(hash, "^[a-fA-F0-9]{64}$")
            || mime is not ("image/x-icon" or "image/png")
            || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != "https" || uri.Host != "raw.githubusercontent.com" || uri.Port != 443
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return false;
        string[] parts = uri.AbsolutePath.Split('/');
        if (parts.Length < 5 || !Regex.IsMatch(parts[1], "^[A-Za-z0-9-]+$")
            || !Regex.IsMatch(parts[2], "^[A-Za-z0-9._-]+$") || !Regex.IsMatch(parts[3], "^[a-fA-F0-9]{40}$")
            || parts.Skip(4).Any(part => part.Length == 0 || part is "." or "..")
            || !uri.AbsolutePath.EndsWith(mime == "image/png" ? ".png" : ".ico", StringComparison.OrdinalIgnoreCase))
            return false;
        source = new(uri, hash.ToLowerInvariant(), mime);
        return true;
    }
}
