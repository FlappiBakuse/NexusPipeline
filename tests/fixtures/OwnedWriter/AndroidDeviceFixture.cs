using NexusPipeline.Extensibility.Execution;
using NexusPipeline.Extensibility.Protocol;

internal static class AndroidDeviceFixture
{
    internal static async Task RunAsync(ExecutionProviderChannel channel, string marker, string mode, CancellationToken cancellation)
    {
        await using var client = await ExecutionDeviceClient.ConnectAsync(channel.Bootstrap, cancellation);
        async Task<ExecutionDeviceResponse> Call(ExecutionDeviceRequest request, bool expected = true)
        {
            var response = await client.InvokeAsync(request, cancellation);
            if (response.Success != expected) throw new IOException("android_device_fixture_failed:" + request.Kind);
            return response;
        }
        _ = await Call(new() { Sequence = 1, Kind = "configure", CaptureMethod = "adb", MouseMethod = "adb", KeyboardMethod = "adb" });
        if (mode is "device-android-timeout" or "device-android-overflow" or "device-android-mapping")
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                _ = await Call(new() { Sequence = 1, Kind = "connect" });
                if (mode != "device-android-mapping")
                    _ = await Call(new() { Sequence = 1, Kind = "shell", Command = mode == "device-android-timeout" ? "device-fixture-hang" : "device-fixture-output",
                        TimeoutMilliseconds = mode == "device-android-timeout" ? 1500 : 60000 });
            }
            catch (IOException)
            {
                await File.WriteAllTextAsync(marker + ".device-denied", elapsed.ElapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellation);
                return;
            }
            throw new IOException("android_device_fixture_limit_not_enforced");
        }
        _ = await Call(new() { Sequence = 1, Kind = "connect" });
        _ = await Call(new() { Sequence = 1, Kind = "connected" });
        if (mode == "device-android-cancel")
        {
            _ = await Call(new() { Sequence = 1, Kind = "shell", Command = "device-fixture-hang", TimeoutMilliseconds = 60000 });
            throw new IOException("android_device_fixture_hang_returned");
        }
        var image = await client.CaptureAsync(cancellation);
        await File.WriteAllBytesAsync(marker + ".device-frame.png", image.ToArray(), cancellation);
        _ = await Call(new() { Sequence = 1, Kind = "click", X = 123, Y = 45 });
        _ = await Call(new() { Sequence = 1, Kind = "swipe", X = 10, Y = 20, X2 = 50, Y2 = 60, DurationMilliseconds = 20 });
        _ = await Call(new() { Sequence = 1, Kind = "key", KeyCode = 4, Phase = 0 });
        _ = await Call(new() { Sequence = 1, Kind = "text", Text = "nexus device" });
        var shell = await Call(new() { Sequence = 1, Kind = "shell", Command = "device-fixture-output", TimeoutMilliseconds = 5000 });
        if (shell.Text != "device-fixture-output") throw new IOException("android_device_fixture_output_changed");
        _ = await Call(new() { Sequence = 1, Kind = "startApp", Intent = "com.nexus.fixture" });
        _ = await Call(new() { Sequence = 1, Kind = "stopApp", Intent = "com.nexus.fixture" });
        _ = await Call(new() { Sequence = 1, Kind = "stopApp", Intent = "com.foreign.app" }, false);
        _ = await Call(new() { Sequence = 1, Kind = "key", KeyCode = 4, Phase = 1 }, false);
        await File.WriteAllTextAsync(marker + ".device-ready", "android-device-completed", cancellation);
    }
}
