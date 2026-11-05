namespace NexusPipeline.Core.Extensions;

public static class ExtensionIdentifiers
{
    public static void Require(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 80 || value[0] is not (>= 'a' and <= 'z') || value[^1] == '-' ||
            value.Contains("--", StringComparison.Ordinal) ||
            value.Any(character => character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
            throw new ArgumentException("extension_identity_invalid");
    }
}
