namespace NexusPipeline.Platform.Extensions;

internal sealed class ExtensionZipChecksum
{
    private static readonly uint[] Table = BuildTable();
    private uint _value = uint.MaxValue;
    internal uint Value => ~_value;
    internal void Append(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes) _value = Table[(_value ^ value) & 255] ^ (_value >> 8);
    }
    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++) value = (value & 1) == 0 ? value >> 1 : (value >> 1) ^ 0xedb88320;
            table[index] = value;
        }
        return table;
    }
}
