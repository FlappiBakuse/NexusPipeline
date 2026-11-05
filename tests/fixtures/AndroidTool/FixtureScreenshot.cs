using System.Buffers.Binary;
using System.IO.Compression;

namespace NexusPipeline.AndroidToolFixture;

internal static class FixtureScreenshot
{
    internal static async Task<int> WriteAsync(Device device, string directory)
    {
        var mode = device.ScreenshotMode ?? "rgb";
        if (mode == "offline")
        {
            Console.Error.Write("error: device offline"); Console.Error.Flush(); return 1;
        }
        var original = Image(mode == "rgba" ? 6 : 2, mode == "compressedTail");
        File.WriteAllBytes(Path.Combine(directory, "fixture-screenshot.png"), original);
        using var output = Console.OpenStandardOutput();
        if (mode == "overflow")
        {
            var block = new byte[65536];
            for (var index = 0; index < 130; index++) await output.WriteAsync(block);
        }
        else await output.WriteAsync(mode == "truncated" ? original.AsMemory(0, original.Length - 7) : original);
        await output.FlushAsync();
        if (mode == "mappingAfter")
        {
            var manager = Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(directory))!, "fixture-device.json");
            var saved = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(manager))!.AsObject();
            saved["reportedPort"] = device.Port + 1;
            File.WriteAllBytes(manager + ".paired-prepared", System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(saved));
            File.Move(manager + ".paired-prepared", manager, overwrite: true);
        }
        if (mode == "hang") await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }

    private static byte[] Image(int color, bool compressedTail)
    {
        using var image = new MemoryStream();
        image.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header, 3);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), 2); header[8] = 8; header[9] = (byte)color;
        Chunk(image, "IHDR"u8, header);
        using var bytes = new MemoryStream();
        using (var zip = new ZLibStream(bytes, CompressionLevel.Fastest, leaveOpen: true))
        {
            for (var y = 0; y < 2; y++)
            {
                zip.WriteByte(0);
                zip.Write(Enumerable.Range(0, 3 * (color == 6 ? 4 : 3)).Select(index => (byte)(index * 17 + y)).ToArray());
            }
        }
        var compressed = bytes.ToArray();
        if (compressedTail) compressed = [.. compressed, 0x42, 0x43];
        Chunk(image, "IDAT"u8, compressed); Chunk(image, "IEND"u8, []);
        return image.ToArray();
    }

    private static void Chunk(Stream output, ReadOnlySpan<byte> kind, ReadOnlySpan<byte> bytes)
    {
        Span<byte> word = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(word, bytes.Length);
        output.Write(word); output.Write(kind); output.Write(bytes);
        var crc = Accumulate(Accumulate(uint.MaxValue, kind), bytes);
        BinaryPrimitives.WriteUInt32BigEndian(word, ~crc); output.Write(word);
    }

    private static uint Accumulate(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? crc >> 1 ^ 0xedb88320U : crc >> 1;
        }
        return crc;
    }
}
