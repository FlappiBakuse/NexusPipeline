using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Extensibility.Execution;
using NexusPipeline.Extensibility.Protocol;

internal static class DeviceReaderFixture
{
    internal static async Task RunAsync(ExecutionProviderChannel channel, string marker, string mode, CancellationToken cancellation)
    {
        if (mode.StartsWith("device-android", StringComparison.Ordinal))
        { await AndroidDeviceFixture.RunAsync(channel, marker, mode, cancellation); return; }
        if (mode == "device-peer")
        {
            var input = marker + ".device-bootstrap"; var report = marker + ".device-peer";
            await File.WriteAllBytesAsync(input, JsonSerializer.SerializeToUtf8Bytes(channel.Bootstrap), cancellation);
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("provider-device-probe"); start.ArgumentList.Add(input); start.ArgumentList.Add(report);
            using var child = Process.Start(start) ?? throw new IOException("device_fixture_child_missing");
            await child.WaitForExitAsync(cancellation);
            if (child.ExitCode != 0 || await File.ReadAllTextAsync(report, cancellation) != "denied") throw new IOException("device_fixture_peer_not_denied");
            return;
        }
        if (mode == "device-sequence")
        {
            await using var pipe = new NamedPipeClientStream(".", channel.Bootstrap.Device!.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(cancellation); _ = await ExecutionProviderFrames.ReadAsync(pipe, 1, cancellation);
            await ExecutionProviderFrames.WriteAsync(pipe, ExecutionProviderFrames.Encode(new ExecutionDeviceRequest { Sequence = 2, Kind = "connected" }, 65536), 65536, cancellation);
            try { _ = await ExecutionProviderFrames.ReadAsync(pipe, 262144, cancellation); }
            catch (IOException) { await File.WriteAllTextAsync(marker + ".device-denied", "sequence", cancellation); return; }
            throw new IOException("device_fixture_sequence_not_denied");
        }
        await using var client = await ExecutionDeviceClient.ConnectAsync(channel.Bootstrap, cancellation);
        async Task<ExecutionDeviceResponse> Call(ExecutionDeviceRequest request, bool expected = true)
        {
            var response = await client.InvokeAsync(request, cancellation);
            if (response.Success != expected) throw new IOException("device_fixture_operation_failed:" + request.Kind);
            return response;
        }
        if (mode == "device-arguments")
        {
            try { _ = await Call(new() { Sequence = 1, Kind = "click", X = 1, Y = 1, Intent = "unrelated.exe" }); }
            catch (IOException) { await File.WriteAllTextAsync(marker + ".device-denied", "arguments", cancellation); return; }
            throw new IOException("device_fixture_arguments_not_denied");
        }
        if (mode is "device-gdi" or "device-screenDc" or "device-framePool" or "device-postMessage")
            _ = await Call(new() { Sequence = 1, Kind = "configure", CaptureMethod = mode == "device-postMessage" ? "printWindow" : mode[7..],
                MouseMethod = "postMessage", KeyboardMethod = "postMessage" });
        if (mode is "device-seize" or "device-cursor" or "device-drag-seize" or "device-drag-cursor")
        {
            var input = mode.EndsWith("seize", StringComparison.Ordinal) ? "seize" : "sendMessageWithCursorPos";
            _ = await Call(new() { Sequence = 1, Kind = "configure", CaptureMethod = "printWindow", MouseMethod = input, KeyboardMethod = input });
        }
        _ = await Call(new() { Sequence = 1, Kind = "connect" });
        if (mode == "device-frozen")
        {
            try { _ = await Call(new() { Sequence = 1, Kind = "configure", CaptureMethod = "gdi", MouseMethod = "sendMessage", KeyboardMethod = "sendMessage" }); }
            catch (IOException) { await File.WriteAllTextAsync(marker + ".device-denied", "frozen", cancellation); return; }
            throw new IOException("device_fixture_configuration_not_frozen");
        }
        _ = await Call(new() { Sequence = 1, Kind = "connected" });
        var png = await client.CaptureAsync(cancellation);
        await File.WriteAllBytesAsync(marker + ".device-frame.png", png.ToArray(), cancellation);
        if (png.Length < 33 || System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.Span[16..]) != 640 ||
            System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.Span[20..]) != 480) throw new IOException("device_fixture_frame_not_original_size");
        DeviceFrameFixture.RequirePixels(png, mode is "device-screenDc" or "device-framePool");
        _ = await Call(new() { Sequence = 1, Kind = "click", X = 123, Y = 45 });
        _ = await Call(new() { Sequence = 1, Kind = "key", KeyCode = 16, Phase = 1 });
        _ = await Call(new() { Sequence = 1, Kind = "key", KeyCode = 65, Phase = 0 });
        _ = await Call(new() { Sequence = 1, Kind = "text", Text = "nexus" });
        _ = await Call(new() { Sequence = 1, Kind = "scroll", Dx = 0, Dy = 120 });
        _ = await Call(new() { Sequence = 1, Kind = "swipe", X = 10, Y = 20, X2 = 50, Y2 = 60, DurationMilliseconds = 20 });
        _ = await Call(new() { Sequence = 1, Kind = "startApp", Intent = "never.exe" }, false);
        _ = await Call(new() { Sequence = 1, Kind = "shell", Command = "never-execute", TimeoutMilliseconds = 100 }, false);
        if (mode is "device-drag" or "device-drag-seize" or "device-drag-cursor")
            _ = await Call(new() { Sequence = 1, Kind = "swipe", X = 80, Y = 90, X2 = 500, Y2 = 300, DurationMilliseconds = 60000 });
        await File.WriteAllTextAsync(marker + ".device-ready", Convert.ToHexStringLower(SHA256.HashData(png.Span)), cancellation);
        if (mode == "device-cancel")
        { try { await Task.Delay(Timeout.InfiniteTimeSpan, channel.Stopping); } catch (OperationCanceledException) when (channel.Stopping.IsCancellationRequested) { } }
    }

    internal static async Task<int> ProbeAsync(string input, string report)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var bootstrap = JsonSerializer.Deserialize<ExecutionProviderBootstrap>(await File.ReadAllBytesAsync(input, timeout.Token))!;
        try { await using var client = await ExecutionDeviceClient.ConnectAsync(bootstrap, timeout.Token); }
        catch (IOException) { await File.WriteAllTextAsync(report, "denied", timeout.Token); return 0; }
        return 81;
    }
}
