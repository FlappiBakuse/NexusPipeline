using System.Buffers.Binary;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal static class WalEvidence
{
    internal static DatabaseWalEvidence Verify(Stream database, Stream wal)
    {
        if (wal.Length == 0) return new(0, 0, 0, 0, 0);
        var header = ReadHeader(database, wal);
        var result = VerifyFrames(wal, header);
        database.Position = 0;
        wal.Position = 0;
        return result;
    }

    private static byte[] ReadHeader(Stream database, Stream wal)
    {
        if (database.Length < 100 || wal.Length < 32) throw new IOException("database_wal_header_incomplete");
        Span<byte> databaseHeader = stackalloc byte[100];
        database.Position = 0;
        database.ReadExactly(databaseHeader);
        var databasePageSize = (uint)BinaryPrimitives.ReadUInt16BigEndian(databaseHeader[16..]);
        if (databasePageSize == 1) databasePageSize = 65536;
        var header = new byte[32];
        wal.Position = 0;
        wal.ReadExactly(header);
        var magic = BinaryPrimitives.ReadUInt32BigEndian(header);
        var pageSize = BinaryPrimitives.ReadUInt32BigEndian(header[8..]);
        if (magic is not (0x377f0682 or 0x377f0683) || BinaryPrimitives.ReadUInt32BigEndian(header[4..]) != 3007000 ||
            pageSize is < 512 or > 65536 || (pageSize & (pageSize - 1)) != 0 || pageSize != databasePageSize ||
            (wal.Length - 32) % (pageSize + 24) != 0)
            throw new IOException("database_wal_format_or_length_unverified");
        uint first = 0, second = 0;
        Update(header.AsSpan(0, 24), magic == 0x377f0683, ref first, ref second);
        VerifyChecksum(header.AsSpan(24), first, second);
        return header;
    }

    private static DatabaseWalEvidence VerifyFrames(Stream wal, byte[] header)
    {
        var pageSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8));
        var bigEndian = BinaryPrimitives.ReadUInt32BigEndian(header) == 0x377f0683;
        var first = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(24));
        var second = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(28));
        var page = new byte[pageSize];
        Span<byte> frame = stackalloc byte[24];
        var invalidated = new WalInvalidatedTail(header, bigEndian);
        long currentFrames = 0, commits = 0;
        while (wal.Position < wal.Length)
        {
            wal.ReadExactly(frame);
            wal.ReadExactly(page);
            if (BinaryPrimitives.ReadUInt32BigEndian(frame) is 0 or uint.MaxValue || BinaryPrimitives.ReadUInt32BigEndian(frame[4..]) == uint.MaxValue)
                throw new IOException("database_wal_frame_page_unverified");
            if (frame.Slice(8, 8).SequenceEqual(header.AsSpan(16, 8)))
            {
                if (invalidated.Frames > 0) throw new IOException("database_wal_generation_resumed_after_tail");
                Update(frame[..8], bigEndian, ref first, ref second);
                Update(page, bigEndian, ref first, ref second);
                VerifyChecksum(frame[16..], first, second);
                currentFrames++;
                if (BinaryPrimitives.ReadUInt32BigEndian(frame[4..]) > 0) commits++;
            }
            else invalidated.Verify(frame, page, first, second);
        }
        return new(pageSize, currentFrames, commits, invalidated.Frames, invalidated.Generations);
    }

    internal static void Update(ReadOnlySpan<byte> bytes, bool bigEndian, ref uint first, ref uint second)
    {
        for (var offset = 0; offset < bytes.Length; offset += 8)
        {
            var left = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]) : BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
            var right = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(bytes[(offset + 4)..]) : BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 4)..]);
            first = unchecked(first + left + second);
            second = unchecked(second + right + first);
        }
    }

    internal static void VerifyChecksum(ReadOnlySpan<byte> bytes, uint first, uint second)
    {
        if (BinaryPrimitives.ReadUInt32BigEndian(bytes) != first || BinaryPrimitives.ReadUInt32BigEndian(bytes[4..]) != second)
            throw new IOException("database_wal_checksum_unverified");
    }
}
