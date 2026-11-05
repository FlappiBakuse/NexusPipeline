using System.Diagnostics;
using System.Text;
using System.Runtime.InteropServices;

internal static class ProcessOutputFixture
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is ["extension-request-digest", var digestInput, var digestReport])
            return ExtensionDigestFixture.Run(digestInput, digestReport);
        if (args is ["probe", var inherited] && long.TryParse(inherited, out var handle))
        {
            var bytes = Encoding.UTF8.GetBytes("unexpected-inherited-output");
            WriteFile(new IntPtr(handle), bytes, (uint)bytes.Length, out _, IntPtr.Zero);
            await Console.OpenStandardOutput().WriteAsync(Encoding.UTF8.GetBytes("own-output"));
            return 0;
        }
        if (args is ["bettergi-restart", var bettergiConfiguration, var bettergiLog])
            return await BetterGiRetryOutput.RunAsync(bettergiConfiguration, bettergiLog);
        if (args is [var adapterMode, var configuration, var adapterLog] && adapterMode.StartsWith("bettergi-", StringComparison.Ordinal))
            return await BetterGiOutput.RunAsync(adapterMode, configuration, adapterLog);
        if (args is [var marchMode, var marchConfiguration, var marchLog] && marchMode.StartsWith("march7th-", StringComparison.Ordinal))
            return await March7thOutput.RunAsync(marchMode, marchConfiguration, marchLog);
        if (args is ["baah-restart", var baahConfiguration, var baahLog])
            return await BaahOutput.RunAsync(baahConfiguration, baahLog);
        if (args is ["baah-unknown", var unknownConfiguration, var unknownLog])
            return await BaahOutput.RunAsync(unknownConfiguration, unknownLog, failFirst: false);
        if (args is ["oknte-restart", var oknteConfiguration, var oknteLog])
            return await OkNteOutput.RunAsync(oknteConfiguration, oknteLog);
        if (args is ["native-resume", var extension, var resumeConfiguration, var resumeLog])
            return await NativeResumeOutput.RunAsync(extension, resumeConfiguration, resumeLog);
        if (args is ["configuration-repeat", var repeatMarker, var repeatedConfiguration])
        {
            OwnedFixtureEvidence.RequirePath(repeatMarker); OwnedFixtureEvidence.RequirePath(repeatedConfiguration);
            var ordinal = File.Exists(repeatMarker) ? int.Parse(File.ReadAllText(repeatMarker), System.Globalization.CultureInfo.InvariantCulture) + 1 : 1;
            File.WriteAllText(repeatMarker, ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
            File.WriteAllBytes(repeatedConfiguration, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { fixture = false, occurrence = ordinal }));
            await Console.OpenStandardOutput().WriteAsync("done\n"u8.ToArray());
            return 0;
        }
        if (args is not [var mode, var marker]) return 2;
        var previouslyInvoked = File.Exists(marker);
        File.WriteAllText(marker, Environment.ProcessId.ToString(), new UTF8Encoding(false));
        using var output = Console.OpenStandardOutput();
        using var error = Console.OpenStandardError();
        if (mode == "release-exit")
        {
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(marker + ".release"))
            { if (deadline.Elapsed > TimeSpan.FromSeconds(10)) return 5; await Task.Delay(1); }
            await output.WriteAsync("released\n"u8.ToArray()); return 37;
        }
        if (mode == "judgement-capacity")
        {
            var line = Encoding.UTF8.GetBytes(new string('<', 4094) + "\n");
            for (var index = 0; index < 768; index++)
            {
                await output.WriteAsync(line); await output.FlushAsync(); await Task.Delay(20);
            }
            File.WriteAllText(marker + ".output-ready.prepared", Environment.ProcessId.ToString(), new UTF8Encoding(false));
            File.Move(marker + ".output-ready.prepared", marker + ".output-ready", overwrite: true);
            while (true)
            {
                await output.WriteAsync("tick\n"u8.ToArray()); await output.FlushAsync(); await Task.Delay(250);
            }
        }
        if (mode == "child-complete")
        {
            _ = InheritedOutputChild.StartReleaseExit(Environment.ProcessPath!, marker + ".child");
            return 0;
        }
        if (mode is "first-partial-timeout" or "first-silent-timeout" or "first-failed")
        {
            if (previouslyInvoked) { await output.WriteAsync(Encoding.UTF8.GetBytes("next\n")); return 37; }
            if (mode == "first-failed") { await output.WriteAsync(Encoding.UTF8.GetBytes("failed\n")); await output.FlushAsync(); }
            while (true)
            {
                if (mode == "first-partial-timeout") { await output.WriteAsync(Encoding.UTF8.GetBytes(".")); await output.FlushAsync(); }
                await Task.Delay(TimeSpan.FromMilliseconds(250));
            }
        }
        if (mode == "utf8-boundary")
        {
            await output.WriteAsync(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("a\n", 32766)) + "a🙂" +
                string.Concat(Enumerable.Repeat("b\n", 32767)) + "b"));
            return 23;
        }
        if (mode == "live-log-stream")
        {
            OwnedFixtureEvidence.RequirePath(marker);
            var line = Encoding.UTF8.GetBytes(new string('a', 1023) + "\n");
            for (var index = 1; index <= 80; index++)
            {
                await output.WriteAsync(line); await output.FlushAsync();
                var deadline = Stopwatch.StartNew();
                while (!File.Exists(marker + ".live-ack-" + index))
                { if (deadline.Elapsed > TimeSpan.FromSeconds(5)) return 5; await Task.Delay(1); }
            }
            return 37;
        }
        if (mode == "history-log-stream")
        {
            var bytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(new string('a', 1023) + "\n", 63)) +
                new string('a', 1023) + "🙂" + string.Concat(Enumerable.Repeat(new string('b', 1023) + "\n", 63)) + "tail");
            for (var offset = 0; offset < bytes.Length; offset += 4096)
            {
                await output.WriteAsync(bytes.AsMemory(offset, Math.Min(4096, bytes.Length - offset)));
                await output.FlushAsync(); await Task.Delay(10);
            }
            return 23;
        }
        if (mode == "split")
        {
            var bytes = Encoding.UTF8.GetBytes("first\r\n中文🙂\r\nlast");
            for (var index = 0; index < bytes.Length; index++)
            {
                await output.WriteAsync(bytes.AsMemory(index, 1)); await output.FlushAsync();
                var deadline = Stopwatch.StartNew();
                while (!File.Exists(marker + ".ack-" + (index + 1).ToString("D4")))
                {
                    if (deadline.Elapsed > TimeSpan.FromSeconds(10)) return 5;
                    await Task.Delay(1);
                }
            }
            await error.WriteAsync(Encoding.UTF8.GetBytes("error\r\n尾")); return 37;
        }
        if (mode == "invalid")
        {
            await output.WriteAsync(new byte[] { 255, 10 });
            await output.WriteAsync(new byte[1048576]);
            await error.WriteAsync(Encoding.UTF8.GetBytes("error-tail"));
            return 19;
        }
        if (mode == "budget-short-child")
        {
            var childId = InheritedOutputChild.StartReleaseExit(Environment.ProcessPath!, marker + ".child");
            using var child = Process.GetProcessById(childId);
            await child.WaitForExitAsync();
            await output.WriteAsync(Encoding.UTF8.GetBytes("child-exit:" + child.ExitCode)); return 37;
        }
        if (mode is "child-root" or "child-wait")
        {
            InheritedOutputChild.Start(Environment.ProcessPath!, marker + ".child");
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(marker + ".child.output-ready"))
            {
                if (deadline.Elapsed > TimeSpan.FromSeconds(10)) return 3;
                await Task.Delay(10);
            }
            await output.WriteAsync(Encoding.UTF8.GetBytes("root-tail"));
            if (mode == "child-wait") await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        }
        if (mode == "wait")
        {
            await output.WriteAsync(Encoding.UTF8.GetBytes("ready\n"));
            await output.FlushAsync();
            File.WriteAllText(marker + ".output-ready.prepared", Environment.ProcessId.ToString(), new UTF8Encoding(false));
            File.Move(marker + ".output-ready.prepared", marker + ".output-ready", overwrite: true);
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        }
        if (mode == "partial-activity")
        {
            for (var index = 0; index < 12; index++)
            {
                await output.WriteAsync(Encoding.UTF8.GetBytes(".")); await output.FlushAsync();
                await Task.Delay(TimeSpan.FromMilliseconds(250));
            }
            return 37;
        }
        var prefix = mode == "bulk" ? new string('x', 1048576) : mode == "many-lines"
            ? string.Concat(Enumerable.Range(0, 4096).Select(index => $"line-{index}\n")) : "ready\n";
        var errorPrefix = mode == "bulk" ? new string('y', 1048576) : mode == "many-lines"
            ? string.Concat(Enumerable.Range(0, 4096).Select(index => $"error-{index}\n")) : "error\n";
        if (mode is not ("bulk" or "tail" or "many-lines")) return 2;
        if (Console.Read() != -1) return 4;
        await Task.WhenAll(output.WriteAsync(Encoding.UTF8.GetBytes(prefix + "中文尾行")).AsTask(),
            error.WriteAsync(Encoding.UTF8.GetBytes(errorPrefix + "错误尾行")).AsTask());
        await output.FlushAsync(); await error.FlushAsync();
        return 37;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(IntPtr handle, byte[] bytes, uint count, out uint written, IntPtr overlapped);
}
