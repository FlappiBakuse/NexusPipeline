using System.Text.Json;

if (args is ["--launch-child", var executable, var childMarker, var childNonce])
{
    var start = new System.Diagnostics.ProcessStartInfo(executable)
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
    start.ArgumentList.Add(childMarker); start.ArgumentList.Add(childNonce);
    using var child = System.Diagnostics.Process.Start(start) ?? throw new IOException("fixture_child_missing");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    while (!File.Exists(childMarker)) await Task.Delay(10, timeout.Token);
    await Task.Delay(300); return 0;
}

if (args.Length is not (2 or 3 or 4)) return 2;
var marker = args[0]; var nonce = args[1];
var observedConfiguration = args.Length >= 3 && args[2] != "-" ? File.ReadAllText(args[2]) : null;
using (var output = new FileStream(marker + ".prepared", FileMode.CreateNew, FileAccess.Write, FileShare.Read))
{
    output.Write(JsonSerializer.SerializeToUtf8Bytes(new { processId = Environment.ProcessId, nonce, observedConfiguration }));
    output.Flush(flushToDisk: true);
}
File.Move(marker + ".prepared", marker);
if (args.Length == 4)
    while (!File.Exists(args[3])) await Task.Delay(20);
else await Task.Delay(Timeout.InfiniteTimeSpan);
return 0;
