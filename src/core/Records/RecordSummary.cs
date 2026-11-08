using NexusPipeline.Core.Execution;

namespace NexusPipeline.Core.Records;

public sealed record RecordSummaryQuery(DateTimeOffset From, DateTimeOffset Until, int OffsetMinutes,
    Guid? UserId = null, BusinessOutcome? Business = null, Guid? AfterUserId = null, LifecycleOutcome? Lifecycle = null)
{
    public void Validate()
    {
        if (From < DateTimeOffset.MinValue.AddHours(14) || Until > DateTimeOffset.MaxValue.AddHours(-14) ||
            From.Offset != TimeSpan.Zero || Until.Offset != TimeSpan.Zero ||
            Until <= From || Until - From > TimeSpan.FromDays(366) || OffsetMinutes is < -840 or > 840 ||
            UserId == Guid.Empty || AfterUserId == Guid.Empty || Business is { } business && !Enum.IsDefined(business) ||
            Lifecycle is { } lifecycle && (!Enum.IsDefined(lifecycle) || lifecycle is LifecycleOutcome.NotStarted or LifecycleOutcome.Running))
            throw new ArgumentException("record_summary_query_invalid");
    }
}

public sealed record RecordTotals(long Runs, long Succeeded, long Failed, long Cancelled, long Unverified,
    long MeasuredCount, long MeasuredMilliseconds, long LowerBoundCount, long UnknownDurationCount);
public sealed record RecordDay(string Date, RecordTotals Totals);
public sealed record RecordUser(Guid? UserId, RecordTotals Totals);
public sealed record RecordDaySummary(RecordTotals Totals, IReadOnlyList<RecordDay> Days);
public sealed record RecordUserPage(IReadOnlyList<RecordUser> Users, Guid? NextUserId);
public sealed record RecordSummary(RecordTotals Totals, IReadOnlyList<RecordDay> Days,
    IReadOnlyList<RecordUser> Users, Guid? NextUserId);
