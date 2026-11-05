namespace NexusPipeline.Platform.Media;

internal static class PngZlibBoundary
{
    private const int MaximumBlocks = 65536;
    private static readonly int[] LengthBase = [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];
    private static readonly int[] LengthExtra = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];
    private static readonly int[] DistanceBase = [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577];
    private static readonly int[] CodeOrder = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];
    private static readonly Huffman FixedLiteral = Fixed(distance: false);
    private static readonly Huffman FixedDistance = Fixed(distance: true);

    internal static void RequireSingleStream(Stream source, long compressedLength, long expectedBytes)
    {
        if (compressedLength is < 6 or > CapturePngSpool.MaximumBytes || expectedBytes < 1) throw Invalid();
        var bits = new Bits(source, compressedLength);
        var cmf = bits.Read(8); var flg = bits.Read(8);
        if ((cmf & 15) != 8 || cmf >> 4 > 7 || ((cmf << 8) | flg) % 31 != 0 || (flg & 32) != 0) throw Invalid();
        var literal = new Huffman(288); var distance = new Huffman(32); var codes = new Huffman(19);
        var workspace = new int[337]; long produced = 0; var count = 0;
        while (true)
        {
            if (++count > MaximumBlocks) throw Invalid();
            var last = bits.Read(1); var kind = bits.Read(2);
            if (kind == 0)
            {
                bits.Align(); var length = bits.Read(16); var inverse = bits.Read(16);
                if ((length ^ inverse) != 65535) throw Invalid();
                bits.SkipAligned(length); produced += length;
            }
            else if (kind is 1 or 2)
            {
                if (kind == 2) Dynamic(bits, literal, distance, codes, workspace);
                ReadCompressed(bits, kind == 1 ? FixedLiteral : literal, kind == 1 ? FixedDistance : distance,
                    ref produced, expectedBytes, 1 << ((cmf >> 4) + 8));
            }
            else throw Invalid();
            if (produced > expectedBytes) throw Invalid();
            if (last == 0) continue;
            if (produced != expectedBytes) throw Invalid();
            bits.Align(); _ = bits.Read(16); _ = bits.Read(16);
            if (bits.Consumed != compressedLength) throw Invalid();
            return;
        }
    }

    private static void ReadCompressed(Bits bits, Huffman literal, Huffman distance, ref long produced,
        long expectedBytes, int window)
    {
        while (true)
        {
            var symbol = literal.Read(bits);
            if (symbol < 256) produced++;
            else if (symbol == 256) return;
            else
            {
                if (symbol is < 257 or > 285) throw Invalid();
                var length = LengthBase[symbol - 257] + bits.Read(LengthExtra[symbol - 257]);
                var code = distance.Read(bits);
                if (code is < 0 or > 29) throw Invalid();
                var offset = DistanceBase[code] + bits.Read(code < 4 ? 0 : code / 2 - 1);
                if (offset > produced || offset > window) throw Invalid();
                produced += length;
            }
            if (produced > expectedBytes) throw Invalid();
        }
    }

    private static void Dynamic(Bits bits, Huffman literal, Huffman distance, Huffman codes, int[] workspace)
    {
        var literalCount = bits.Read(5) + 257; var distanceCount = bits.Read(5) + 1; var codeCount = bits.Read(4) + 4;
        if (literalCount > 286) throw Invalid();
        var codeLengths = workspace.AsSpan(0, 19); codeLengths.Clear();
        for (var index = 0; index < codeCount; index++) codeLengths[CodeOrder[index]] = bits.Read(3);
        codes.Reset(codeLengths);
        var lengths = workspace.AsSpan(19, literalCount + distanceCount); var position = 0;
        while (position < lengths.Length)
        {
            var symbol = codes.Read(bits);
            if (symbol < 16) { lengths[position++] = symbol; continue; }
            int value, repeat;
            if (symbol == 16)
            {
                if (position == 0) throw Invalid();
                value = lengths[position - 1]; repeat = bits.Read(2) + 3;
            }
            else if (symbol == 17) { value = 0; repeat = bits.Read(3) + 3; }
            else if (symbol == 18) { value = 0; repeat = bits.Read(7) + 11; }
            else throw Invalid();
            if (repeat > lengths.Length - position) throw Invalid();
            lengths.Slice(position, repeat).Fill(value); position += repeat;
        }
        if (lengths[256] == 0) throw Invalid();
        literal.Reset(lengths[..literalCount]); distance.Reset(lengths[literalCount..]);
    }

    private static Huffman Fixed(bool distance)
    {
        var lengths = new int[distance ? 32 : 288];
        if (distance) lengths.AsSpan().Fill(5);
        else
        {
            lengths.AsSpan(0, 144).Fill(8); lengths.AsSpan(144, 112).Fill(9);
            lengths.AsSpan(256, 24).Fill(7); lengths.AsSpan(280, 8).Fill(8);
        }
        var result = new Huffman(lengths.Length); result.Reset(lengths); return result;
    }

    private sealed class Huffman(int capacity)
    {
        private readonly int[] _counts = new int[16], _firstCode = new int[16], _firstSymbol = new int[16], _next = new int[16];
        private readonly int[] _symbols = new int[capacity];

        internal void Reset(ReadOnlySpan<int> lengths)
        {
            if (lengths.Length > _symbols.Length) throw Invalid();
            Array.Clear(_counts);
            foreach (var length in lengths)
            {
                if (length is < 0 or > 15) throw Invalid();
                if (length != 0) _counts[length]++;
            }
            var left = 1; var code = 0; var cursor = 0;
            for (var length = 1; length <= 15; length++)
            {
                left = (left << 1) - _counts[length]; if (left < 0) throw Invalid();
                code = (code + _counts[length - 1]) << 1;
                _firstCode[length] = code; _firstSymbol[length] = cursor; _next[length] = cursor; cursor += _counts[length];
            }
            for (var symbol = 0; symbol < lengths.Length; symbol++)
            {
                var length = lengths[symbol];
                if (length != 0) _symbols[_next[length]++] = symbol;
            }
        }

        internal int Read(Bits bits)
        {
            var code = 0;
            for (var length = 1; length <= 15; length++)
            {
                code = (code << 1) | bits.Read(1);
                var offset = code - _firstCode[length];
                if ((uint)offset < (uint)_counts[length]) return _symbols[_firstSymbol[length] + offset];
            }
            throw Invalid();
        }
    }

    private sealed class Bits(Stream source, long length)
    {
        private readonly byte[] _buffer = new byte[4096];
        private uint _bits;
        private int _bitCount, _position, _count;
        private long _loaded;
        internal long Consumed { get; private set; }

        internal int Read(int count)
        {
            while (_bitCount < count)
            {
                EnsureData(); _bits |= (uint)_buffer[_position++] << _bitCount; _bitCount += 8; Consumed++;
            }
            var value = (int)(_bits & ((1u << count) - 1)); _bits >>= count; _bitCount -= count; return value;
        }

        internal void Align() { _bits = 0; _bitCount = 0; }

        internal void SkipAligned(int count)
        {
            if (_bitCount != 0) throw Invalid();
            while (count > 0)
            {
                EnsureData(); var take = Math.Min(count, _count - _position);
                _position += take; Consumed += take; count -= take;
            }
        }

        private void EnsureData()
        {
            if (_position < _count) return;
            if (_loaded >= length) throw Invalid();
            _count = source.Read(_buffer.AsSpan(0, (int)Math.Min(_buffer.Length, length - _loaded)));
            if (_count == 0) throw Invalid();
            _loaded += _count; _position = 0;
        }
    }

    private static IOException Invalid() => new("capture_png_zlib_invalid");
}
