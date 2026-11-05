using System.Buffers.Binary;
using NexusPipeline.Core.Automation;
using NexusPipeline.Platform.Media;

namespace NexusPipeline.Platform.Storage.Automation;

internal static class UserAvatar
{
    internal static string Require(string value)
    {
        if (value.Length == 0) return value;
        const string prefix = "data:image/png;base64,";
        try
        {
            if (!value.StartsWith(prefix, StringComparison.Ordinal) || value.Length > 65536) throw Invalid();
            var bytes = Convert.FromBase64String(value[prefix.Length..]);
            if (bytes.Length is < 57 or > 49152 || Convert.ToBase64String(bytes) != value[prefix.Length..] ||
                BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16)) is < 1 or > 96 ||
                BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20)) is < 1 or > 96) throw Invalid();
            using var input = new MemoryStream(bytes, writable: false);
            _ = BoundedCapturePng.Verify(input, allowRgba: true, allowAncillary: true);
            return value;
        }
        catch (Exception error) when (error is IOException or FormatException or ArgumentException or OverflowException)
        {
            throw Invalid();
        }
    }

    private static AutomationDefinitionException Invalid() => new("automation_avatar_invalid");
}
