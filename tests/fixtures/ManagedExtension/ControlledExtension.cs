using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Extensibility.Lifecycle;
using NexusPipeline.Extensibility.Media;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Extensibility.Scheduling;
using NexusPipeline.Extensibility.Frontend;

namespace Controlled;

public sealed class ManagedExtension(ExtensionActivationContext context) : IExtension, IExtensionFrontend
{
    private string? _directory;
    public IReadOnlyList<FrontendOperation> Operations { get; } =
    [new("read-state", FrontendAccess.Read), new("save-state", FrontendAccess.Write),
        new("reveal", FrontendAccess.RevealSecret), new("login", FrontendAccess.Desktop),
        new("wait", FrontendAccess.Read), new("bad-json", FrontendAccess.Read),
        new("doc-read", FrontendAccess.Read), new("doc-write", FrontendAccess.Write),
        new("doc-commit", FrontendAccess.Read), new("http-request", FrontendAccess.Write),
        new("cover", FrontendAccess.Read), new("image-read", FrontendAccess.Read),
        new("image-list", FrontendAccess.Read), new("image-write", FrontendAccess.Write),
        new("desktop-session", FrontendAccess.Read), new("secret-save", FrontendAccess.Write), new("secret-commit", FrontendAccess.Read),
        new("notification-targets", FrontendAccess.Read), new("manual-run", FrontendAccess.Write),
        new("manual-quiesce", FrontendAccess.Write), new("manual-leak", FrontendAccess.Write), new("manual-current", FrontendAccess.Read),
        new("manual-clock", FrontendAccess.Read), new("manual-state", FrontendAccess.Read), new("manual-notify", FrontendAccess.Write),
        new("native-require", FrontendAccess.Desktop), new("native-read", FrontendAccess.Read), new("native-state", FrontendAccess.Read),
        new("native-capture", FrontendAccess.Desktop), new("native-windows", FrontendAccess.Desktop)];

    public async ValueTask<FrontendResult> InvokeAsync(FrontendCall call, CancellationToken cancellationToken)
    {
        if (call.Operation == "native-windows")
            return new("application/json", JsonSerializer.SerializeToUtf8Bytes(await context.Desktop!.ReadWindowsAsync(cancellationToken)));
        if (call.Operation == "native-capture") return await ControlledBrowserLogin.CaptureAsync(context, call, _directory!, cancellationToken);
        if (call.Operation == "native-state")
            return new("application/json", JsonSerializer.SerializeToUtf8Bytes(await context.Desktop!.ReadClientAsync(cancellationToken)));
        if (call.Operation is "native-require" or "native-read")
        {
            await context.Desktop!.RequireClientAsync(cancellationToken);
            return new("application/json", "{\"accepted\":true}"u8.ToArray());
        }
        if (call.Operation.StartsWith("manual-", StringComparison.Ordinal))
            return await ControlledManualWork.InvokeAsync(context, call, _directory!, cancellationToken);
        if (call.Operation == "notification-targets")
            return new("application/json", JsonSerializer.SerializeToUtf8Bytes(await context.NotificationTargets!.ReadAsync(cancellationToken)));
        if (call.Operation is "secret-save" or "secret-commit")
            return await ControlledSecretDocs.InvokeAsync(context, call, _directory!, cancellationToken);
        if (call.Operation == "desktop-session")
            return new("application/json", JsonSerializer.SerializeToUtf8Bytes(await context.Desktop!.ReadSessionAsync(cancellationToken)));
        if (call.Operation.StartsWith("image-", StringComparison.Ordinal))
            return await ControlledImages.InvokeAsync(context, call, cancellationToken);
        if (call.Operation.StartsWith("doc-", StringComparison.Ordinal) || call.Operation == "http-request")
            return await ControlledDataOperations.InvokeAsync(context, call, cancellationToken);
        if (call.Operation == "wait")
        {
            File.WriteAllText(Path.Combine(_directory!, "frontend-entered"), "entered");
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            finally { File.WriteAllText(Path.Combine(_directory!, "frontend-stopped"), "stopped"); }
        }
        if (call.Operation == "cover")
        {
            using var input = JsonDocument.Parse(call.Payload);
            if (input.RootElement.GetProperty("assetId").GetString() != "one") throw new IOException("fixture_image_missing");
            return new("image/png", Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jC1sAAAAASUVORK5CYII="));
        }
        if (call.Operation == "bad-json") return new("application/json", "{invalid"u8.ToArray());
        return new("application/json", JsonSerializer.SerializeToUtf8Bytes(new
        { call.Operation, call.OperationId, call.SubjectId, call.IsLocal, call.ClientId }));
    }

    public async ValueTask ActivateAsync(IExtensionScope scope, CancellationToken cancellationToken)
    {
        await using var assets = await context.PackageAssets.AcquireAsync(cancellationToken);
        using var inputStream = assets.OpenRead("activation-input.json");
        var input = await JsonSerializer.DeserializeAsync<Input>(inputStream, cancellationToken: cancellationToken)
            ?? throw new IOException("fixture_input_missing");
        if (!Path.IsPathFullyQualified(input.Directory) || input.Nonce.Length != 32) throw new IOException("fixture_input_invalid");
        var directory = Path.GetFullPath(input.Directory);
        _directory = directory;
        if (File.Exists(Path.Combine(directory, "compiler-mode")))
            await context.Compilers!.RegisterAsync("project", new ControlledCompiler(directory), cancellationToken);
        if (File.Exists(Path.Combine(directory, "manual-task.json")))
            await context.Schedules.RegisterAsync("manual", new ControlledManualWork(context, directory), cancellationToken);
        if (input.Mode == "asset-escape") _ = assets.File("../activation-input.json");
        if (input.Mode.StartsWith("native", StringComparison.Ordinal))
        {
            await ControlledNativeLibrary.ActivateAsync(input.Mode, directory, assets, scope);
            return;
        }
        string? outputPath = null;
        if (input.Mode == "output")
        {
            var output = await context.RuntimeOutputs.CreateAsync("fixture-log", cancellationToken);
            outputPath = output.Path;
            WriteNew(Path.Combine(outputPath, "actual-output.txt"), Encoding.UTF8.GetBytes(input.Nonce));
        }
        var resource = new Resource(directory, input.Nonce, input.Mode == "throw-cleanup");
        await scope.OwnAsync(resource).ConfigureAwait(false);
        WriteNew(Path.Combine(directory, "activated.json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            input.Nonce, SdkAssembly = typeof(IExtension).Assembly.FullName,
            SdkLocation = typeof(IExtension).Assembly.Location, outputPath
        }));
        if (input.Mode == "frontend-ready") await scope.OwnAsync(new ControlledInitializer(context, scope.Cancellation, directory));
        if (input.Mode == "schedule-wait")
        {
            var handler = new HeldSchedule(context, input, directory);
            await scope.OwnAsync(handler);
            await context.Schedules.RegisterAsync("daily", handler, cancellationToken);
        }
        if (input.Mode == "media-wait")
            await context.MediaPreviews.RegisterAsync("held", new HeldProvider(directory), cancellationToken);
        if (input.Mode == "media-frame")
            await context.MediaPreviews.RegisterAsync("frame", new FixedProvider(), cancellationToken);
        if (input.Mode is "throw" or "throw-cleanup") throw new InvalidOperationException("controlled_activation_failure");
        if (input.Mode == "wait") await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        if (input.Mode == "late")
        {
            var deadline = Environment.TickCount64 + 30000;
            while (!File.Exists(Path.Combine(directory, "release")) && Environment.TickCount64 < deadline)
                await Task.Delay(20, CancellationToken.None).ConfigureAwait(false);
            if (!File.Exists(Path.Combine(directory, "release"))) throw new IOException("fixture_release_missing");
        }
        if (input.Mode is not ("return" or "throw" or "throw-cleanup" or "wait" or "late" or "media-wait" or "media-frame" or "output" or "schedule-wait" or "frontend" or "frontend-ready"))
            throw new IOException("fixture_mode_unknown");
    }

    private static void WriteNew(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        stream.Write(bytes); stream.Flush(flushToDisk: true);
    }

    private sealed class FixedProvider : IMediaFrameProvider
    {
        public ValueTask<MediaFrameContent> CaptureAsync(MediaCapture request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/4t8AAAAASUVORK5CYII=");
            return ValueTask.FromResult(new MediaFrameContent(new MediaFrame
            {
                CapturedAt = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                Format = "nexus.media.frame", Version = 1, Capture = request, MimeType = "image/png", Width = 1, Height = 1,
                SizeBytes = bytes.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes))
            }, new MemoryStream(bytes, writable: false)));
        }
    }

    private sealed class HeldProvider(string directory) : IMediaFrameProvider
    {
        public async ValueTask<MediaFrameContent> CaptureAsync(MediaCapture request, CancellationToken cancellationToken)
        {
            WriteNew(Path.Combine(directory, "media-entered"), [1]);
            var deadline = Environment.TickCount64 + 30000;
            while (!File.Exists(Path.Combine(directory, "media-release")) && Environment.TickCount64 < deadline)
                await Task.Delay(20, CancellationToken.None);
            throw new IOException("fixture_media_callback_finished_without_frame");
        }
    }

    private sealed record Input(string Directory, string Nonce, string Mode, string? ScheduledTaskId = null, DateTimeOffset? ScheduledUtc = null);
    private sealed class HeldSchedule(ExtensionActivationContext context, Input input, string directory)
        : IScheduledWorkPlanningHandler, IAsyncDisposable
    {
        private Task? _late;
        public ValueTask<ScheduledWorkPlans> ReadPlansAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var when = input.ScheduledUtc ?? throw new IOException("fixture_schedule_time_missing");
            return ValueTask.FromResult(new ScheduledWorkPlans { Format = "nexus.schedule.work-plans", Version = 1, Revision = 1,
                Tasks = [new() { TaskId = input.ScheduledTaskId!, Revision = 1, IsEnabled = true, TimeZoneId = "UTC",
                    Weekdays = [(long)when.DayOfWeek], LocalMinute = when.Hour * 60 + when.Minute, TimeoutSeconds = 120,
                    NotificationTaskIds = [] }] });
        }
        public ValueTask<ScheduledWorkResult> ExecuteAsync(ScheduledInvocation invocation, CancellationToken cancellationToken) =>
            throw new IOException("fixture_unplanned_execution_forbidden");
        public async ValueTask<ScheduledWorkResult> ExecutePlannedAsync(ScheduledPlannedInvocation invocation, CancellationToken cancellationToken)
        {
            if (invocation.PlansRevision != 1 || invocation.TaskRevision != 1)
                throw new IOException("fixture_schedule_revision_changed");
            WriteNew(Path.Combine(directory, "scheduled-context.json"), JsonSerializer.SerializeToUtf8Bytes(invocation));
            WriteNew(Path.Combine(directory, "scheduled-entered"), [1]);
            await Wait("scheduled-release");
            try
            {
                await using var assets = await context.PackageAssets.AcquireAsync(CancellationToken.None);
                using var stream = assets.OpenRead("activation-input.json");
                WriteNew(Path.Combine(directory, "scheduled-borrowed"), [checked((byte)Math.Min(stream.Length, 255))]);
            }
            catch (IOException)
            {
                WriteNew(Path.Combine(directory, "scheduled-borrow-denied"), [1]);
                throw;
            }
            _late = Task.Run(async () =>
            {
                await Wait("scheduled-late-release");
                try
                {
                    await using var assets = await context.PackageAssets.AcquireAsync(CancellationToken.None);
                    _ = assets.File("activation-input.json");
                    WriteNew(Path.Combine(directory, "scheduled-late-effect"), [1]);
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
                { WriteNew(Path.Combine(directory, "scheduled-late-rejected"), [1]); }
            });
            return new() { Format = "nexus.schedule.work-result", Version = 1, Invocation = invocation.Invocation,
                BusinessOutcome = "notApplicable", ReasonCode = "fixture_installed_schedule_returned" };
        }
        private async Task Wait(string marker)
        {
            var deadline = Environment.TickCount64 + 30000;
            while (!File.Exists(Path.Combine(directory, marker)) && Environment.TickCount64 < deadline)
                await Task.Delay(20, CancellationToken.None);
            if (!File.Exists(Path.Combine(directory, marker))) throw new IOException("fixture_schedule_release_missing");
        }
        public async ValueTask DisposeAsync()
        {
            File.WriteAllText(Path.Combine(directory, "scheduled-late-release"), "release");
            if (_late is not null) await _late;
        }
    }
    private sealed class Resource : IAsyncDisposable
    {
        private readonly FileStream _handle;
        private readonly string _directory, _nonce;
        private readonly bool _failCleanup;
        private int _disposed;
        internal Resource(string directory, string nonce, bool failCleanup)
        {
            _directory = directory; _nonce = nonce; _failCleanup = failCleanup;
            _handle = new(Path.Combine(directory, "resource.bin"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            _handle.Write(Encoding.UTF8.GetBytes(nonce)); _handle.Flush(flushToDisk: true);
        }
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _handle.Dispose();
                WriteNew(Path.Combine(_directory, "cleaned.json"), JsonSerializer.SerializeToUtf8Bytes(new { Nonce = _nonce }));
                if (_failCleanup) throw new IOException("controlled_cleanup_failure");
            }
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class NoContextExtension : IExtension
{
    public ValueTask ActivateAsync(IExtensionScope scope, CancellationToken cancellationToken)
        => throw new IOException("fixture_no_context_callback_ran");
}
