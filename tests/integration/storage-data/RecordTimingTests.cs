using NexusPipeline.Core.Records;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class RecordTimingTests
{
    [Theory(DisplayName = "Paging Preserves Explicit Timing Quality And Rejects Invented Durations")]
    [InlineData(RecordDurationKind.Measured)]
    [InlineData(RecordDurationKind.CheckpointLowerBound)]
    [InlineData(RecordDurationKind.Unknown)]
    public void PreservesTimingQuality(RecordDurationKind kind)
    {
        var (_, store) = RecordStoreFixture.NewStore();
        var record = RecordStoreFixture.Record(1, Guid.NewGuid()) with
        {
            DurationMilliseconds = kind == RecordDurationKind.Unknown ? null : 17,
            DurationKind = kind,
            EndTimeKind = kind == RecordDurationKind.Measured ? EndTimeKind.MeasuredTerminal : EndTimeKind.ReconciledTerminalObservation
        };
        store.AppendCompleted([record]);
        Assert.Throws<ArgumentException>(() => store.AppendCompleted([record with
        {
            DurationMilliseconds = kind == RecordDurationKind.Unknown ? 0 : null
        }]));
        Assert.Throws<ArgumentException>(() => store.AppendCompleted([record with
        {
            EndTimeKind = kind == RecordDurationKind.Measured ? EndTimeKind.ReconciledTerminalObservation : EndTimeKind.MeasuredTerminal
        }]));
        Assert.Equal(record, Assert.Single(store.ReadPage(new()).Items));
    }
}
