using System.Text;

internal static class LogOutputFixture
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is not [var mode, var marker, var directory]) return 2;
        using var stdout = Console.OpenStandardOutput();
        await stdout.WriteAsync("pipe-success\n"u8.ToArray()); await stdout.FlushAsync();
        Publish(marker + ".ready"); await Wait(marker + ".go");
        var path = Path.Combine(directory, "events.log");
        switch (mode)
        {
            case "long-stdout":
                await WriteLongAsync(stdout, (byte)'p', 20);
                break;
            case "long-file":
                using (var file = Open(path, FileMode.Append)) await WriteLongAsync(file, (byte)'f', 25);
                break;
            case "missing": return 37;
            case "append": await Append(path, "file-failure\n"); break;
            case "wait":
                await Append(path, "file-failure\n"); Publish(marker + ".written");
                await Task.Delay(Timeout.InfiniteTimeSpan); break;
            case "split":
                using (var file = Open(path, FileMode.Append))
                    foreach (var value in Encoding.UTF8.GetBytes("file-中文🙂-failure\r\nfinal-tail"))
                    { await file.WriteAsync(new[] { value }); await file.FlushAsync(); await Task.Delay(40); }
                break;
            case "replace":
                var created = File.GetCreationTimeUtc(path); File.Delete(path);
                await File.WriteAllTextAsync(path, "file-failure\n", new UTF8Encoding(false)); File.SetCreationTimeUtc(path, created);
                break;
            case "rewrite":
                using (var file = Open(path, FileMode.Open))
                { file.SetLength(0); await file.WriteAsync("file-failure\n"u8.ToArray()); file.Flush(true); }
                break;
            case "rotate":
                await Append(path, "first\n"); Publish(marker + ".first"); await Wait(marker + ".ack-first");
                File.Move(path, Path.Combine(directory, "retired.log")); await Append(path, "second\n");
                Publish(marker + ".second"); await Wait(marker + ".ack-second");
                await Append(Path.Combine(directory, "retired.log"), "file-failure\n");
                break;
            case "latest":
                path = Path.Combine(directory, "older.log"); await Append(path, "file-failure\n");
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5)); break;
            case "date":
                var dated = Path.Combine(directory, DateTime.Now.ToString("yyyy-MM-dd")); Directory.CreateDirectory(dated);
                await Append(Path.Combine(dated, "run-current.log"), "file-failure\n"); break;
            case "date-tree":
                var tree = Path.Combine(directory, DateTime.Now.ToString("yyyy/MM/dd")); Directory.CreateDirectory(tree);
                await Append(Path.Combine(tree, "run-current.log"), "file-failure\n"); break;
            case "activity":
                for (var index = 0; index < 16; index++) { await Append(path, "."); await Task.Delay(250); }
                break;
            case "invalid":
                using (var file = Open(path, FileMode.Append)) { await file.WriteAsync(new byte[] { 255, 10 }); await file.WriteAsync("file-failure\n"u8.ToArray()); }
                break;
            case "locked":
                using (var file = new FileStream(Path.Combine(directory, "locked.log"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
                { Publish(marker + ".locked"); await Task.Delay(Timeout.InfiniteTimeSpan); }
                break;
            case "overflow":
                using (var file = Open(path, FileMode.Append))
                {
                    var line = Encoding.UTF8.GetBytes(new string('x', 4094) + "\n");
                    for (var index = 0; index < 5632; index++) await file.WriteAsync(line);
                }
                break;
            default: return 2;
        }
        Publish(marker + ".written"); return 37;
    }

    private static FileStream Open(string path, FileMode mode) => new(path, mode, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
    private static async Task WriteLongAsync(Stream output, byte content, int delayMilliseconds)
    {
        var block = new byte[65536];
        for (var index = 0; index < 832; index++)
        {
            Array.Fill(block, content);
            System.Text.Encoding.ASCII.GetBytes(index.ToString("D8"), block.AsSpan(0, 8));
            for (var end = 4095; end < block.Length; end += 4096) block[end] = (byte)'\n';
            await output.WriteAsync(block); await output.FlushAsync();
            await Task.Delay(delayMilliseconds);
        }
    }
    private static async Task Append(string path, string text)
    {
        using var file = Open(path, FileMode.Append); await file.WriteAsync(Encoding.UTF8.GetBytes(text)); file.Flush(true);
    }
    private static void Publish(string path)
    { File.WriteAllText(path + ".prepared", Environment.ProcessId.ToString()); File.Move(path + ".prepared", path); }
    private static async Task Wait(string path)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (!File.Exists(path))
        { if (deadline.Elapsed > TimeSpan.FromSeconds(30)) throw new IOException("file_log_fixture_deadline"); await Task.Delay(10); }
    }
}
