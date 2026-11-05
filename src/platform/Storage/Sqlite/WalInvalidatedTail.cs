using System.Buffers.Binary;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal sealed class WalInvalidatedTail(byte[] header, bool bigEndian)
{
    private readonly uint _headerSalt = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16));
    private readonly uint _headerRandomSalt = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20));
    private uint _age, _randomSalt, _first, _second;
    internal long Frames { get; private set; }
    internal long Generations { get; private set; }

    internal void Verify(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> page, uint currentFirst, uint currentSecond)
    {
        var salt = BinaryPrimitives.ReadUInt32BigEndian(frame[8..]);
        var randomSalt = BinaryPrimitives.ReadUInt32BigEndian(frame[12..]);
        var age = unchecked(_headerSalt - salt);
        if (age is 0 or > int.MaxValue || randomSalt == _headerRandomSalt || Frames > 0 && age < _age)
            throw new IOException("database_wal_frame_generation_unverified");
        if (Frames == 0)
        {
            WalEvidence.Update(frame[..8], bigEndian, ref currentFirst, ref currentSecond);
            WalEvidence.Update(page, bigEndian, ref currentFirst, ref currentSecond);
            if (BinaryPrimitives.ReadUInt32BigEndian(frame[16..]) == currentFirst && BinaryPrimitives.ReadUInt32BigEndian(frame[20..]) == currentSecond)
                throw new IOException("database_wal_current_frame_salts_corrupt");
        }
        if (Frames > 0 && age == _age)
        {
            if (randomSalt != _randomSalt) throw new IOException("database_wal_tail_generation_ambiguous");
            WalEvidence.Update(frame[..8], bigEndian, ref _first, ref _second);
            WalEvidence.Update(page, bigEndian, ref _first, ref _second);
            WalEvidence.VerifyChecksum(frame[16..], _first, _second);
        }
        else Generations++;
        // A reset overwrote each old generation's prefix. Its first surviving checksum is a chain seed, never current data.
        _first = BinaryPrimitives.ReadUInt32BigEndian(frame[16..]);
        _second = BinaryPrimitives.ReadUInt32BigEndian(frame[20..]);
        _age = age;
        _randomSalt = randomSalt;
        Frames++;
    }
}
