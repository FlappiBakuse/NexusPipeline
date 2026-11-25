namespace NexusPipeline.Platform.Storage.Extensions;

internal static class LocalExtensionEvidence
{
    internal static bool IsDigest(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static bool ValidId(string id) => id.StartsWith("local-", StringComparison.Ordinal) &&
        id.AsSpan(6).Contains('-');
}
