using System.Text.Json;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage.Maintenance;

namespace NexusPipeline.Platform.Security;

internal sealed record ReleaseTrustConfiguration(ExtensionReleaseTrust Extensions, UpdateReleaseTrust Updates)
{
    private static readonly Lazy<ReleaseTrustConfiguration?> Embedded = new(ReadEmbedded);
    internal static ReleaseTrustConfiguration? Current => Embedded.Value;
    private const string FixtureKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE6uyjBJaylz5W+08fYBHMupD7vnjEOsNOFQGlLksjWMd+NAK85+0rrHkg8zoubm64PNjUvrMV7JOhQKRVUdWDBg==";

    internal static ReleaseTrustConfiguration? Read(ReadOnlyMemory<byte> original)
    {
        if (original.Length is < 1 or > 16384) throw Invalid();
        try
        {
            using var document = JsonDocument.Parse(original, new() { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.GetProperty("format").GetString() != "nexus.release.trust" || root.GetProperty("version").GetInt32() != 1) throw Invalid();
            if (root.GetProperty("state").GetString() == "unconfigured")
            {
                Fields(root, "format", "version", "state");
                return null;
            }
            Fields(root, "format", "version", "state", "keys", "storeRoot", "feedRoot", "artifactRoots",
                "storeStableFloor", "storeDevelopFloor", "updateStableFloor", "updatePrereleaseFloor");
            if (root.GetProperty("state").GetString() != "configured") throw Invalid();
            var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var key in root.GetProperty("keys").EnumerateObject())
            {
                var bytes = Convert.FromBase64String(key.Value.GetString()!);
                if (key.Name.Contains("test", StringComparison.OrdinalIgnoreCase) || key.Name.Contains("fixture", StringComparison.OrdinalIgnoreCase) ||
                    bytes.AsSpan().SequenceEqual(Convert.FromBase64String(FixtureKey)) || !keys.TryAdd(key.Name, bytes)) throw Invalid();
            }
            var store = Root(root.GetProperty("storeRoot").GetString()!);
            var feed = Root(root.GetProperty("feedRoot").GetString()!);
            var artifacts = root.GetProperty("artifactRoots").EnumerateArray().Select(value => Root(value.GetString()!)).ToArray();
            var extensions = new ExtensionReleaseTrust(keys, store, artifacts,
                root.GetProperty("storeStableFloor").GetInt64(), root.GetProperty("storeDevelopFloor").GetInt64());
            var updates = new UpdateReleaseTrust(keys, feed, store, artifacts,
                root.GetProperty("updateStableFloor").GetInt64(), root.GetProperty("updatePrereleaseFloor").GetInt64());
            return new(extensions, updates);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or
            ArgumentException or FormatException or System.Security.Cryptography.CryptographicException)
        { throw new IOException("release_trust_configuration_invalid", error); }
    }

    private static void Fields(JsonElement value, params string[] expected)
    {
        var actual = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != expected.Length || actual.Distinct(StringComparer.Ordinal).Count() != actual.Length ||
            !actual.ToHashSet(StringComparer.Ordinal).SetEquals(expected)) throw Invalid();
    }

    private static Uri Root(string value)
    {
        var result = new Uri(value, UriKind.Absolute);
        if (result.IdnHost.EndsWith(".test", StringComparison.OrdinalIgnoreCase) ||
            result.IdnHost.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase) ||
            result.IdnHost.EndsWith(".example", StringComparison.OrdinalIgnoreCase) ||
            result.IdnHost is "example.com" or "example.org" or "example.net") throw Invalid();
        return result;
    }

    private static ReleaseTrustConfiguration? ReadEmbedded()
    {
        using var input = typeof(ReleaseTrustConfiguration).Assembly.GetManifestResourceStream("Security.ReleaseTrust.json") ?? throw Invalid();
        using var output = new MemoryStream();
        input.CopyTo(output);
        return Read(output.ToArray());
    }

    private static IOException Invalid() => new("release_trust_configuration_invalid");
}
