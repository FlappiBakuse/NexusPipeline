using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Media;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class CaptureWorkerTests
{
    [Fact(DisplayName = "Batch Frame Encoder Writes A Valid Png And Enforces Its Byte Limit")]
    public void WritesBoundedFrames()
    {
        using var output = new WindowCaptureWorker.BoundedFrameBuffer();
        var row = new byte[640 * 4];
        for (var index = 0; index < row.Length; index += 4) { row[index] = 255; row[index + 3] = 255; }
        PngScanlineEncoder.Write(output, 640, 480, (_, destination) => row.CopyTo(destination, 0));
        Assert.InRange(output.Length, 57, 8_388_608);
        var originalLength = output.Length;
        output.Position = 0; RgbCapturePng.Verify(output, 640, 480); output.Position = originalLength;
        var bytes = new byte[65_536];
        while (output.Length < 8_388_608)
            output.Write(bytes, 0, checked((int)Math.Min(bytes.Length, 8_388_608 - output.Length)));
        Assert.Equal(8_388_608, output.Length);
        Assert.Throws<IOException>(() => output.Write(bytes, 0, 1));
        Assert.Throws<IOException>(() => output.Write(bytes.AsSpan(0, 1)));
        Assert.Throws<IOException>(() => output.WriteByte(0));
    }

    [Fact(DisplayName = "Batch Frame Admission Fits The Existing Image Catalog Capacity")]
    public void BoundsCatalogSize()
    {
        Assert.True(ExecutionCaptureInputs.FitsCatalog(1, 4096));
        Assert.True(ExecutionCaptureInputs.FitsCatalog(200, 20));
        Assert.False(ExecutionCaptureInputs.FitsCatalog(200, 21));
        Assert.False(ExecutionCaptureInputs.FitsCatalog(1, 4097));
    }

    [Theory(DisplayName = "Actual Isolated Window Capture Returns The Owned Window Pixels And Strict Nonce")]
    [InlineData(null)]
    [InlineData("gdi")]
    [InlineData("screenDc")]
    [InlineData("framePool")]
    public async Task CapturesOwnedWindow(string? method)
    {
        await using var fixture = new CaptureFixture();
        var request = (await fixture.Ready(method == "screenDc")) with { CaptureMethod = method };
        var result = await fixture.Capture(request, TimeSpan.FromSeconds(10));
        Assert.True(result.ExitCode == 0, File.ReadAllText(Path.Combine(fixture.Root, "capture-stderr.bin")));
        Assert.True(result.Stopped); Assert.False(result.TimedOut);
        using var input = File.OpenRead(result.Path); var prefix = new byte[32]; input.ReadExactly(prefix);
        Assert.True(prefix.AsSpan(0, 8).SequenceEqual("NXSCAP01"u8));
        Assert.Equal(request.RequestId, new Guid(prefix.AsSpan(8, 16)));
        var width = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(24));
        var height = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(28));
        Assert.Equal(fixture.Width, width); Assert.Equal(fixture.Height, height);
        var pngPath = Path.Combine(fixture.Root, "actual-window.png");
        using (var png = new FileStream(pngPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) await input.CopyToAsync(png);
        using var pngInput = File.OpenRead(pngPath); RgbCapturePng.Verify(pngInput, width, height);
        var signature = new byte[8]; pngInput.ReadExactly(signature);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, signature);
        using var idat = new MemoryStream();
        while (true)
        {
            var header = new byte[8]; pngInput.ReadExactly(header);
            var length = BinaryPrimitives.ReadInt32BigEndian(header); Assert.InRange(length, 0, 65536);
            var bytes = new byte[length]; pngInput.ReadExactly(bytes); var crc = new byte[4]; pngInput.ReadExactly(crc);
            if (header.AsSpan(4).SequenceEqual("IHDR"u8))
            { Assert.Equal(width, BinaryPrimitives.ReadInt32BigEndian(bytes)); Assert.Equal(height, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(4))); }
            if (header.AsSpan(4).SequenceEqual("IDAT"u8)) idat.Write(bytes);
            if (header.AsSpan(4).SequenceEqual("IEND"u8)) break;
        }
        idat.Position = 0;
        using var decompressed = new ZLibStream(idat, CompressionMode.Decompress); var row = new byte[width * 3 + 1];
        // Compositor captures include the rounded window corners.
        var inset = method is "screenDc" or "framePool" ? 16 : 0;
        for (var y = 0; y < height; y++)
        {
            decompressed.ReadExactly(row); Assert.Equal(0, row[0]);
            Assert.Equal(new byte[] { 0, 0, 255 }, row.AsSpan(1 + inset * 3, 3).ToArray());
            Assert.Equal(new byte[] { 255, 0, 0 }, row.AsSpan(1 + (width - 1 - inset) * 3, 3).ToArray());
        }
        Assert.Equal(-1, decompressed.ReadByte());
        Assert.InRange(new FileInfo(pngPath).Length, 33, 8388608);
    }

    [Theory(DisplayName = "An Isolated Capture Rejects Wrong Native Identity Without Any Png")]
    [InlineData(null)]
    [InlineData("framePool")]
    public async Task RejectsWrongIdentity(string? method)
    {
        await using var fixture = new CaptureFixture();
        var request = (await fixture.Ready()) with { CaptureMethod = method };
        var result = await fixture.Capture(request with { Target = request.Target with { ImageSha256 = new string('0', 64) } }, TimeSpan.FromSeconds(10));
        Assert.Equal(1, result.ExitCode); Assert.True(result.Stopped); Assert.False(result.TimedOut);
        Assert.Equal(0, new FileInfo(result.Path).Length);
    }

    [Fact(DisplayName = "A Stalled Capture Request Is Stopped As A Complete Owned Helper Family")]
    public async Task StopsStalledHelpers()
    {
        await using var fixture = new CaptureFixture();
        var request = await fixture.Ready();
        var elapsed = Stopwatch.StartNew();
        var result = await fixture.Capture(request, TimeSpan.FromSeconds(2), completeInput: false);
        Assert.True(result.TimedOut); Assert.True(result.Stopped); Assert.Null(result.ExitCode);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Equal(0, new FileInfo(result.Path).Length);
    }

    [Fact(DisplayName = "Screen Capture Rejects A Different Owned Window Covering The Selected Client Area")]
    public async Task RejectsOccludedWindow()
    {
        await using var target = new CaptureFixture();
        var request = (await target.Ready(screen: true)) with { CaptureMethod = "screenDc" };
        await using var covering = new CaptureFixture();
        _ = await covering.Ready(screen: true);
        var result = await target.Capture(request, TimeSpan.FromSeconds(10));
        Assert.Equal(1, result.ExitCode); Assert.True(result.Stopped); Assert.False(result.TimedOut);
        Assert.Equal(0, new FileInfo(result.Path).Length);
        Assert.Equal("capture_window_occluded", File.ReadAllText(Path.Combine(target.Root, "capture-stderr.bin")).Trim());
    }

    private sealed record CaptureResult(string Path, int? ExitCode, bool TimedOut, bool Stopped);

    private sealed class CaptureFixture : IAsyncDisposable
    {
        private readonly OwnedCrashProcess _window = new();
        internal string Root { get; } = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "native-window-capture-" + Guid.NewGuid().ToString("N"));
        internal int Width { get; private set; }
        internal int Height { get; private set; }
        internal CaptureFixture() { Directory.CreateDirectory(Root); }
        internal async Task<WindowCaptureRequest> Ready(bool screen = false)
        {
            var report = Path.Combine(Root, "window.json");
            var native = _window.Start(screen ? ["capture-window-fixture", report, "screen-frame"] : ["capture-window-fixture", report],
                Root, Guid.NewGuid(), Guid.NewGuid(), Path.Combine(Root, "window-native.json"));
            var observed = await OwnedCrashProcess.ReadReportAsync(report, TimeSpan.FromSeconds(15));
            Assert.Equal("windowReady", observed.GetProperty("status").GetString());
            Assert.Equal(native.ProcessId, observed.GetProperty("processId").GetInt32());
            Width = observed.GetProperty("width").GetInt32(); Height = observed.GetProperty("height").GetInt32();
            Assert.InRange(Width, 64, 1024); Assert.InRange(Height, 64, 1024);
            return new("nexus.media.window-capture-request", 1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                new(native.ProcessId, native.CreatedAtUtc, native.ImagePath, native.ImageSha256, native.UserSid, native.IsElevated),
                observed.GetProperty("windowHandle").GetInt64());
        }
        internal async Task<CaptureResult> Capture(WindowCaptureRequest request, TimeSpan timeout, bool completeInput = true)
        {
            var helper = Environment.GetEnvironmentVariable("NEXUS_TEST_HOST_EXE")!;
            var image = ExecutionHookImages.Capture(helper, runtime: true); var held = new List<IDisposable>();
            using var job = new OwnedWorkerJob(); job.ObserveProcessIdentities(_ => { });
            using var output = new OwnedProcessOutput(redirectInput: true);
            var path = Path.Combine(Root, "capture.bin");
            Task reading = Task.CompletedTask, errors = Task.CompletedTask;
            int? code = null; var timedOut = false; var stopped = false;
            ProcessIdentity? helperRoot = null;
            try
            {
                PlanInputLease.AcquireImage(image, held);
                var native = job.Start(helper, ["--window-capture-worker"], Path.GetDirectoryName(helper)!, request.InstanceId, request.AttemptId,
                    identity => File.WriteAllBytes(Path.Combine(Root, "capture-native.json"), JsonSerializer.SerializeToUtf8Bytes(identity)), output);
                helperRoot = new(native.ProcessId, native.CreatedAtUtc, native.ImagePath, native.ImageSha256, native.UserSid, native.IsElevated);
                reading = Drain(output.StandardOutput, path, 8388640);
                errors = Drain(output.StandardError, Path.Combine(Root, "capture-stderr.bin"), 65536);
                await output.StandardInput.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(request, WindowCaptureWorker.Options));
                if (completeInput) output.CompleteInput();
                code = await job.AwaitRootAsync(native, timeout);
                timedOut = code is null;
            }
            finally
            {
                var proof = await PreparedStoppedTarget.StopAsync(job, TimeSpan.FromSeconds(5));
                stopped = proof.Use(job, inventory =>
                {
                    var observations = inventory.AllFamily.Select(member => new { Identity = member,
                        State = WriterStopEvidence.ObserveProcess(member) }).ToArray();
                    File.WriteAllBytes(Path.Combine(Root, "capture-stop.json"), JsonSerializer.SerializeToUtf8Bytes(new { inventory, observations }));
                    return helperRoot is not null && inventory.AllFamily.Contains(helperRoot) &&
                        inventory.TotalProcesses == inventory.AllFamily.Length && inventory.ActiveProcesses == 0 &&
                        inventory.LivingFamily.IsEmpty && inventory.RetainedKernelObjects == inventory.AllFamily.Length &&
                        observations.All(member => member.State == WriterState.Stopped);
                });
                await Task.WhenAll(reading, errors).WaitAsync(TimeSpan.FromSeconds(5));
                foreach (var item in held.AsEnumerable().Reverse()) item.Dispose();
            }
            return new(path, code, timedOut, stopped);
        }
        private static async Task Drain(Stream input, string path, long maximum)
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            var buffer = new byte[8192]; long observed = 0;
            while (true)
            {
                var count = await input.ReadAsync(buffer); if (count == 0) break;
                observed += count; if (observed > maximum) throw new IOException("capture_fixture_output_limit");
                await file.WriteAsync(buffer.AsMemory(0, count));
            }
            file.Flush(flushToDisk: true);
        }
        public async ValueTask DisposeAsync()
        {
            try { var stopped = await _window.StopAsync(); Assert.True(stopped.CompleteJobStopped); }
            finally { _window.Dispose(); }
        }
    }
}
