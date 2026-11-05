using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Media;
using NexusPipeline.Extensibility.Lifecycle;
using NexusPipeline.Extensibility.Media;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;

internal static class MediaPluginFixture
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is not [var inputPath, var reportPath]) return 2;
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new IOException("Missing fixture root"));
        using var input = JsonDocument.Parse(File.ReadAllBytes(Owned(root, inputPath)));
        var settings = input.RootElement;
        var assemblyPath = Owned(root, settings.GetProperty("assembly").GetString()!);
        var imagePath = Owned(root, settings.GetProperty("image").GetString()!);
        var manifestPath = Owned(root, settings.GetProperty("manifest").GetString()!);
        reportPath = Owned(root, reportPath);
        using var imageLease = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var imageSha = Convert.ToHexStringLower(SHA256.HashData(imageLease));
        Require(imageSha == settings.GetProperty("imageSha256").GetString(), "Image input differs");
        var png = File.ReadAllBytes(imagePath);
        var width = settings.GetProperty("width").GetInt32();
        var height = settings.GetProperty("height").GetInt32();
        var reader = new ContractReader();
        using var plugin = new PluginSliceAssembly(assemblyPath, settings.GetProperty("assemblySha256").GetString()!, manifestPath, "live-screenshot", reader);
        var manifestBytes = plugin.ManifestBytes;
        var type = plugin.ExtensionType;
        Require(typeof(IExtension).IsAssignableFrom(type), "Plugin entry does not implement current SDK");
        var context = new RunMediaContext(settings.GetProperty("instanceId").GetGuid(), settings.GetProperty("runId").GetGuid(), settings.GetProperty("attemptId").GetGuid());
        var authorization = new Authorization(context);
        var source = new ScreenshotSource(new PreviewImage(width, height, png));
        var registry = new MediaPreviewRegistry(authorization);
        RunScreenshotPort? retainedScreenshotPort = null;
        var activator = new ExtensionActivator(reader);
        IExtension Factory(ExtensionManifest descriptor, ExtensionScope scope)
        {
            var previews = new MediaRegistrationPort(descriptor, scope, registry, reader);
            retainedScreenshotPort = new RunScreenshotPort(descriptor, scope, source, authorization, reader);
            return (IExtension)(Activator.CreateInstance(type, SliceActivationContext.Create(previews: previews, screenshots: retainedScreenshotPort)) ?? throw new IOException("Plugin factory returned null"));
        }
        var pendingWasHidden = false;
        await using var activation = await activator.ActivateManagedAsync(manifestBytes, "live-screenshot", (descriptor, scope) =>
            new ActivationProbe(Factory(descriptor, scope), () =>
            {
                try { using var unexpected = registry.Open(context, "live-screenshot", "current-task"); }
                catch (InvalidOperationException) { pendingWasHidden = true; }
                Require(pendingWasHidden, "Partial plugin registration was visible");
            }), default);
        using var preview = registry.Open(context, "live-screenshot", "current-task");
        Require(source.Calls == 0, "Plugin started unrequested capture");
        var first = await preview.CaptureAsync();
        Require(first.Png.Span.SequenceEqual(png) && source.Calls == 1, "Plugin did not return the requested frame");
        var wrongContextRejected = false;
        try { using var unexpected = registry.Open(new(context.InstanceId, Guid.NewGuid(), context.AttemptId), "live-screenshot", "current-task"); }
        catch (UnauthorizedAccessException) { wrongContextRejected = true; }
        Require(wrongContextRejected && source.Calls == 1, "Wrong run reached the screenshot source");
        source.BlockNext();
        var late = preview.CaptureAsync().AsTask();
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        preview.Dispose();
        source.Release.SetResult();
        var lateRejected = await IsRejected(late);
        Require(lateRejected, "Closed preview accepted a late frame");
        using var next = registry.Open(context, "live-screenshot", "current-task");
        source.BlockNext();
        var revoked = next.CaptureAsync().AsTask();
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        authorization.Allowed = false;
        source.Release.SetResult();
        var runRevocationRejected = await IsRejected(revoked);
        Require(runRevocationRejected, "Revoked run accepted a late frame");
        authorization.Allowed = true;
        await activation.DisposeAsync();
        var oldPortRejected = false;
        try { await retainedScreenshotPort!.CaptureAsync(Request(context), default); }
        catch (OperationCanceledException) { oldPortRejected = true; }
        Require(oldPortRejected, "Retained screenshot port outlived activation");
        var failedActivationRejected = false;
        try
        {
            await activator.ActivateManagedAsync(manifestBytes, "live-screenshot", (descriptor, scope) =>
                new ActivationProbe(Factory(descriptor, scope), () => throw new InvalidOperationException("controlled_activation_failure")), default);
        }
        catch (InvalidOperationException exception) when (exception.Message == "controlled_activation_failure") { failedActivationRejected = true; }
        Require(failedActivationRejected, "Controlled activation failure did not propagate");
        await using var reactivated = await activator.ActivateManagedAsync(manifestBytes, "live-screenshot", Factory, default);
        using var restoredPreview = registry.Open(context, "live-screenshot", "current-task");
        Require((await restoredPreview.CaptureAsync()).Png.Span.SequenceEqual(png), "Rollback prevented clean reactivation");
        var report = new
        {
            status = "PASS", scope = "Actual independently packaged SDK and plugin DLL in an owned fixture worker; screenshot input is synthetic",
            processId = Environment.ProcessId, assembly = assemblyPath, assemblySha256 = plugin.AssemblySha256, references = plugin.References,
            contractFingerprint = NexusPipeline.Extensibility.SdkIdentity.ContractFingerprint, context,
            imageSha256 = imageSha, width, height, sizeBytes = png.Length, captureCalls = source.Calls,
            pendingWasHidden, wrongContextRejected, lateRejected, runRevocationRejected, oldPortRejected, failedActivationRejected,
            reactivationCaptured = true
        };
        File.WriteAllBytes(reportPath, JsonSerializer.SerializeToUtf8Bytes(report, JsonSerializerOptions.Web));
        return 0;
    }

    private static MediaCapture Request(RunMediaContext context) => new()
    {
        Format = "nexus.media.capture", Version = 1, InstanceId = context.InstanceId.ToString("D"), RunId = context.RunId.ToString("D"),
        AttemptId = context.AttemptId.ToString("D"), PreviewId = Guid.NewGuid().ToString("D"), RequestId = Guid.NewGuid().ToString("D"), Sequence = 1
    };

    private static async Task<bool> IsRejected(Task<PreviewImage> capture)
    {
        try { await capture.WaitAsync(TimeSpan.FromSeconds(5)); return false; }
        catch (OperationCanceledException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private static string Owned(string root, string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Fixture path leaves the owned root");
        for (var current = new FileInfo(full).Directory; current is not null && !string.Equals(current.FullName, root, StringComparison.OrdinalIgnoreCase); current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Fixture path has a reparse parent");
        if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new IOException("Fixture file is a reparse point");
        return full;
    }

    private static void Require(bool condition, string reason) { if (!condition) throw new IOException(reason); }

    private sealed class ActivationProbe(IExtension inner, Action activated) : IExtension
    {
        public async ValueTask ActivateAsync(IExtensionScope scope, CancellationToken cancellationToken)
        {
            await inner.ActivateAsync(scope, cancellationToken);
            activated();
        }
    }

    private sealed class Authorization(RunMediaContext current) : IRunMediaAuthorization
    {
        public bool Allowed { get; set; } = true;
        public void AssertAllowed(RunMediaContext context, string extensionId)
        {
            if (!Allowed || context != current || extensionId != "live-screenshot") throw new UnauthorizedAccessException("fixture_run_media_access_denied");
        }
    }

    private sealed class ScreenshotSource(PreviewImage image) : IMediaFrameSource
    {
        private bool _block;
        public int Calls { get; private set; }
        public TaskCompletionSource Entered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void BlockNext()
        {
            _block = true;
            Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        public async ValueTask<PreviewImage> CaptureAsync(MediaCaptureRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            if (_block)
            {
                _block = false;
                Entered.SetResult();
                await Release.Task;
            }
            return image;
        }
    }
}
