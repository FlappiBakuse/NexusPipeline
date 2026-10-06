using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NexusPipeline.Host.Desktop;

internal static class DesktopSupervisorProtocol
{
    internal static void Fields(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).SequenceEqual(names.Order(StringComparer.Ordinal)))
            throw new InvalidDataException("Invalid desktop message fields");
    }
    internal static string Proof(byte[] key, string direction, string root, string session, string clientNonce, string hostNonce, string instance, string buildId)
        => Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(string.Join('\n', direction, "1", root, "g0170", session, clientNonce, hostNonce, instance, buildId)))).ToLowerInvariant();
    internal static bool EqualsProof(string supplied, string expected)
        => supplied.Length == 64 && supplied.All(char.IsAsciiHexDigitLower) && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(supplied), Convert.FromHexString(expected));
    internal static string RootHash(string root)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).TrimEnd('\\', '/').ToUpperInvariant()))).ToLowerInvariant();
    internal static string PipeName(string rootHash) => "NexusPipeline.g0170." + rootHash[..24];
}
