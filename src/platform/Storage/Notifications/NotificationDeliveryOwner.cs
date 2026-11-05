using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Sqlite;
using System.Text.Json.Serialization;

namespace NexusPipeline.Platform.Storage.Notifications;

internal sealed record NotificationDeliveryOwner(
    [property: JsonRequired] Guid WriterId, [property: JsonRequired] int ProcessId,
    [property: JsonRequired] long CreatedAtFileTimeUtc,
    [property: JsonRequired] string ImagePath, [property: JsonRequired] string ImageSha256,
    [property: JsonRequired] string UserSid, [property: JsonRequired] bool IsElevated)
{
    internal static NotificationDeliveryOwner Capture(JournalWriter actual) => new(actual.WriterId,
        actual.Process.ProcessId, actual.Process.CreatedAtUtc.ToFileTimeUtc(), actual.Process.ImagePath,
        actual.Process.ImageSha256, actual.Process.UserSid, actual.Process.IsElevated);

    internal ProcessIdentity ToProcessIdentity()
    {
        if (WriterId == Guid.Empty || ProcessId < 1 || CreatedAtFileTimeUtc < 1 ||
            string.IsNullOrWhiteSpace(ImagePath) || ImagePath.Length > 32767 ||
            PhysicalPathPolicy.NormalizeSyntax(ImagePath) != ImagePath || ImageSha256 is null ||
            ImageSha256.Length != 64 || ImageSha256.Any(c => c is not (>= 'a' and <= 'f' or >= '0' and <= '9')) ||
            string.IsNullOrEmpty(UserSid) || !UserSid.StartsWith("S-1-", StringComparison.Ordinal))
            throw new IOException("notification_delivery_sender_unbound");
        try { return new(ProcessId, DateTime.FromFileTimeUtc(CreatedAtFileTimeUtc), ImagePath, ImageSha256, UserSid, IsElevated); }
        catch (ArgumentOutOfRangeException error) { throw new IOException("notification_delivery_sender_unbound", error); }
    }
}
