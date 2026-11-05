namespace NexusPipeline.Platform.Storage.Sqlite;

internal static class JournalWriterPolicy
{
    internal static void Require(JournalWriter writer)
    {
        if (writer is null || writer.Process is not { } process || writer.WriterId == Guid.Empty || process.ProcessId < 1 ||
            process.CreatedAtUtc.Kind != DateTimeKind.Utc || process.CreatedAtUtc.ToFileTimeUtc() < 1 ||
            string.IsNullOrEmpty(process.UserSid) || !process.UserSid.StartsWith("S-1-", StringComparison.Ordinal) ||
            process.UserSid.Length > 184 || process.UserSid.Contains('\0') || string.IsNullOrEmpty(process.ImagePath) ||
            process.ImagePath.Length > 32767 || PhysicalPathPolicy.NormalizeSyntax(process.ImagePath) != process.ImagePath ||
            !FileEffectRows.IsDigest(process.ImageSha256)) throw new IOException("journal_writer_identity_invalid");
    }
}
