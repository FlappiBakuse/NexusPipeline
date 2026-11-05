using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Media;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

internal static class CatalogCapacityData
{
    internal const int RecordCount = 100000, PngCount = 5000, Owners = 100, ImagesPerOwner = 50;
    internal static readonly DateTimeOffset Anchor = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid[] Automations = Enumerable.Range(0, 20).Select(number => Id(3, number)).ToArray();
    private static readonly Guid[] Users = Enumerable.Range(0, 50).Select(number => Id(4, number)).ToArray();
    private static readonly BusinessOutcome[] Outcomes = [BusinessOutcome.Succeeded, BusinessOutcome.Failed, BusinessOutcome.Partial, BusinessOutcome.Unverified];

    internal static Guid RunId(int number) => Id(1, number);
    private static DateTimeOffset Ended(int number) => Anchor.AddDays(number % 31).AddMinutes(number / 31 % 1440);
    private static BusinessOutcome Business(int number) => Outcomes[number / 1000 % Outcomes.Length];

    internal static CompletedRunRecord Record(int number) => new(Automations[number % 20], Users[number / 20 % 50],
        Ended(number), number % 60000, new(RunId(number), Id(2, number), 1,
            Business(number) == BusinessOutcome.Failed ? EngineOutcome.Failed : EngineOutcome.Succeeded,
            Business(number), LifecycleOutcome.Completed, RecoveryOutcome.NotRequired, "controlled_storage_record"));

    internal static void WriteRecords(RunRecordStore store)
    {
        var batch = new List<CompletedRunRecord>(256);
        for (var number = 0; number < RecordCount; number++)
        {
            batch.Add(Record(number));
            if (batch.Count != 256 && number != RecordCount - 1) continue;
            store.AppendCompleted(batch); batch.Clear();
        }
    }

    internal static (long Bytes, RecordAsset First, RecordAsset[] Samples) WritePngs(ImmutableBlobStore blobs, RecordAssetStore assets)
    {
        long total = 0; RecordAsset? first = null; var samples = new List<RecordAsset>(3);
        for (var number = 0; number < PngCount; number++)
        {
            using var png = new MemoryStream(65536); uint state = (uint)(number + 1) ^ 0x9e3779b9U;
            PngScanlineEncoder.Write(png, 160, 90, (_, row) =>
            {
                for (var index = 0; index < row.Length; index++)
                { state ^= state << 13; state ^= state >> 17; state ^= state << 5; row[index] = (byte)state; }
            });
            Assert.InRange(png.Length, 57L, 65536L);
            png.Position = 0; var sha = Convert.ToHexStringLower(SHA256.HashData(png)); png.Position = 0;
            var reference = new BlobReference($"blobs/content/{sha}.bin", png.Length, sha);
            blobs.PersistContent(png, reference, CancellationToken.None);
            var asset = new RecordAsset(RunId(number / ImagesPerOwner), sha, png.Length, RecordAssetKind.Screenshot);
            assets.AttachVerified(asset); first ??= asset; total += png.Length;
            if (number is 0 or 1234 or 4999) samples.Add(asset);
        }
        return (total, first!, samples.ToArray());
    }

    internal static IReadOnlyList<(string Name, RecordQuery Query)> Queries()
    {
        var from = Anchor.AddDays(6); var until = Anchor.AddDays(23);
        return [("all", new(200)), ("date", new(200, EndedFromInclusive: from, EndedUntilExclusive: until)),
            ("automation", new(137, AutomationId: Automations[3], EndedFromInclusive: from, EndedUntilExclusive: until)),
            ("user", new(113, UserId: Users[17], EndedFromInclusive: from, EndedUntilExclusive: until)),
            ("business", new(199, Business: BusinessOutcome.Failed, EndedFromInclusive: from, EndedUntilExclusive: until)),
            ("combined", new(73, Automations[3], Users[17], BusinessOutcome.Failed, EndedFromInclusive: from, EndedUntilExclusive: until)),
            ("fromOnly", new(200, EndedFromInclusive: Anchor.AddDays(30))),
            ("untilOnly", new(200, EndedUntilExclusive: Anchor.AddDays(1)))];
    }

    internal static int Scan(RunRecordStore store, RecordQuery query)
    {
        var expected = Enumerable.Range(0, RecordCount).Count(number => Matches(number, query));
        var count = 0; CompletedRunRecord? previous = null; RecordCursor? cursor = null;
        do
        {
            var page = store.ReadPage(query with { After = cursor });
            Assert.InRange(page.Items.Count, 0, query.Limit);
            foreach (var record in page.Items)
            {
                var number = int.Parse(record.FinalAttempt.RunId.ToString("N")[20..], NumberStyles.HexNumber, CultureInfo.InvariantCulture) - 1;
                Assert.InRange(number, 0, RecordCount - 1); Assert.True(Matches(number, query)); Assert.Equal(Record(number), record);
                if (previous is not null) Assert.True(previous.EndedAt > record.EndedAt ||
                    previous.EndedAt == record.EndedAt && StringComparer.Ordinal.Compare(
                        previous.FinalAttempt.RunId.ToString("D"), record.FinalAttempt.RunId.ToString("D")) > 0);
                previous = record; count++;
            }
            cursor = page.Next;
        } while (cursor is not null);
        Assert.Equal(expected, count); return count;
    }

    private static bool Matches(int number, RecordQuery query) =>
        (query.AutomationId is null || query.AutomationId == Automations[number % 20]) &&
        (query.UserId is null || query.UserId == Users[number / 20 % 50]) &&
        (query.Business is null || query.Business == Business(number)) &&
        (query.EndedFromInclusive is null || Ended(number) >= query.EndedFromInclusive) &&
        (query.EndedUntilExclusive is null || Ended(number) < query.EndedUntilExclusive);

    private static Guid Id(int domain, int number)
    {
        Span<byte> bytes = stackalloc byte[16]; bytes.Clear();
        BinaryPrimitives.WriteInt32BigEndian(bytes, domain); BinaryPrimitives.WriteInt32BigEndian(bytes[12..], number + 1);
        return new Guid(bytes, bigEndian: true);
    }
}
