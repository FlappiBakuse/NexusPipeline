using NexusPipeline.Core.Execution;

namespace NexusPipeline.Core.Records;

public sealed record AttemptHistoryQuery(Guid RunId, int AfterOrdinal = 0, int Limit = 50);
public sealed record AttemptHistoryPage(IReadOnlyList<AttemptReceipt> Items, int? NextOrdinal);

public interface IAttemptHistoryReader
{
    AttemptHistoryPage ReadPage(AttemptHistoryQuery query);
}

public static class AttemptHistoryPolicy
{
    public static void Validate(AttemptHistoryQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.RunId == Guid.Empty || query.AfterOrdinal is < 0 or > 99 || query.Limit is < 1 or > 100)
            throw new ArgumentException("attempt_history_query_invalid");
    }
}
