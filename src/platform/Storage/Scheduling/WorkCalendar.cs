namespace NexusPipeline.Platform.Storage.Scheduling;

internal static class WorkCalendar
{
    internal static DateOnly Date(DateTimeOffset observedAt, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(observedAt, zone).DateTime);

    internal static void Require(DateTimeOffset observedAt, string zoneId, DateOnly date)
    {
        if (observedAt.Offset != TimeSpan.Zero || observedAt <= DateTimeOffset.UnixEpoch || zoneId is not { Length: >= 1 and <= 128 } ||
            zoneId.Contains('\0') || Date(observedAt, TimeZoneInfo.FindSystemTimeZoneById(zoneId)) != date)
            throw new IOException("work_calendar_unbound");
    }
}
