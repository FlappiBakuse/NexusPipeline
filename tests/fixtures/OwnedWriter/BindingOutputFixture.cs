using System.Text.Json;

internal static class BindingOutputFixture
{
    internal static async Task WriteAsync(string role, string marker)
    {
        var path = marker + ".output-fixture.json";
        if (!File.Exists(path)) return;
        OwnedFixtureEvidence.RequirePath(path);
        using var configuration = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
        var value = configuration.RootElement;
        var count = value.GetProperty(role + "Bytes").GetInt32();
        if (count is < 0 or > 8388608) throw new IOException("fixture_output_size_invalid");
        if (role == "main")
        {
            var log = value.GetProperty("logPath").GetString()!;
            OwnedFixtureEvidence.RequirePath(log);
            var bytes = value.GetProperty("logBytes").GetInt32();
            if (bytes is < 0 or > 8388608) throw new IOException("fixture_log_size_invalid");
            await using var file = new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            await WriteBlocksAsync(file, bytes, (byte)'l');
            await file.WriteAsync("done\n"u8.ToArray());
            await file.FlushAsync();
        }
        await WriteBlocksAsync(Console.OpenStandardOutput(), count, (byte)'p');
    }

    private static async Task WriteBlocksAsync(Stream output, int count, byte content)
    {
        var block = new byte[2048];
        Array.Fill(block, content); block[^1] = (byte)'\n';
        var sinceDelay = 0;
        while (count > 0)
        {
            var length = Math.Min(block.Length, count);
            await output.WriteAsync(block.AsMemory(0, length)); count -= length;
            sinceDelay += length;
            if (sinceDelay >= 4096) { await Task.Delay(20); sinceDelay = 0; }
        }
        await output.FlushAsync();
    }
}
