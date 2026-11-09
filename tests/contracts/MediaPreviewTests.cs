using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NexusPipeline.Core.Media;
using NexusPipeline.Extensibility;
using NexusPipeline.Extensibility.Lifecycle;
using NexusPipeline.Extensibility.Media;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using Xunit;

namespace NexusPipeline.ContractTests;

public sealed class MediaPreviewTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/4t8AAAAASUVORK5CYII=");
    private static RunMediaContext Context() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    [Fact(DisplayName = "Media Provider Is Invisible Until Publication And Captures Only On Demand")]
    public async Task PublishesOnDemand()
    {
        var authorization = new Authorization();
        var registry = new MediaPreviewRegistry(authorization);
        var provider = new Provider();
        var context = Context();
        await using var scope = await Activate(registry, provider, afterRegistration: () =>
            Assert.Throws<InvalidOperationException>(() => registry.Open(context, "fixture", "preview")));
        using var preview = registry.Open(context, "fixture", "preview");
        Assert.Equal(0, provider.Calls);
        var first = await preview.CaptureAsync();
        Assert.Equal(Png, first.Png.ToArray());
        Assert.Equal(1, first.Width);
        Assert.True(provider.LastStream!.Disposed);
        var firstRequest = provider.LastRequest!;
        await preview.CaptureAsync();
        Assert.Equal(firstRequest.PreviewId, provider.LastRequest!.PreviewId);
        Assert.NotEqual(firstRequest.RequestId, provider.LastRequest.RequestId);
        Assert.True(provider.LastRequest.Sequence > firstRequest.Sequence);
        Assert.Equal(context.InstanceId.ToString("D"), provider.LastRequest.InstanceId);
        Assert.Equal(context.RunId.ToString("D"), provider.LastRequest.RunId);
        Assert.Equal(context.AttemptId.ToString("D"), provider.LastRequest.AttemptId);
    }

    [Theory(DisplayName = "Late Media Frames Are Disposed And Cannot Outlive Their Preview Extension Or Run")]
    [InlineData("preview")]
    [InlineData("extension")]
    [InlineData("run")]
    public async Task RejectsLateFrames(string ended)
    {
        var authorization = new Authorization();
        var registry = new MediaPreviewRegistry(authorization);
        var provider = new Provider { Block = true };
        var scope = await Activate(registry, provider);
        using var preview = registry.Open(Context(), "fixture", "preview");
        var pending = preview.CaptureAsync().AsTask();
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => preview.CaptureAsync().AsTask());
        if (ended == "preview") preview.Dispose();
        if (ended == "extension") await scope.DisposeAsync();
        if (ended == "run") authorization.Allowed = false;
        provider.Release.SetResult();
        var failure = await Record.ExceptionAsync(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        if (ended == "run") Assert.IsType<UnauthorizedAccessException>(failure);
        else Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.True(provider.LastStream!.Disposed);
        await scope.DisposeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => preview.CaptureAsync().AsTask());
    }

    [Fact(DisplayName = "Failed Media Activation Rolls Back And Retained Ports Cannot Register Late Providers")]
    public async Task RollsBackActivation()
    {
        var registry = new MediaPreviewRegistry(new Authorization());
        MediaRegistrationPort? retained = null;
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Activate(registry, new Provider(), true,
            retain: port => retained = port));
        Assert.Throws<InvalidOperationException>(() => registry.Open(Context(), "fixture", "preview"));
        await Assert.ThrowsAsync<OperationCanceledException>(() => retained!.RegisterAsync("late", new Provider(), default).AsTask());
        await using var next = await Activate(registry, new Provider());
        using var preview = registry.Open(Context(), "fixture", "preview");
        Assert.Equal(Png, (await preview.CaptureAsync()).Png.ToArray());
    }

    [Fact(DisplayName = "Media Capability Is Checked Before Any Provider Can Be Registered")]
    public async Task RequiresCaptureCapability()
    {
        var registry = new MediaPreviewRegistry(new Authorization());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await Activate(registry, new Provider(), declared: false));
        Assert.Throws<InvalidOperationException>(() => registry.Open(Context(), "fixture", "preview"));
    }

    [Theory(DisplayName = "Invalid Media Cannot Publish A Frame And Always Releases Its Transferred Stream")]
    [InlineData("request")]
    [InlineData("run")]
    [InlineData("instance")]
    [InlineData("attempt")]
    [InlineData("preview")]
    [InlineData("sequence")]
    [InlineData("generation")]
    [InlineData("digest")]
    [InlineData("dimensions")]
    [InlineData("excess")]
    [InlineData("truncated")]
    [InlineData("budget")]
    public async Task RejectsInvalidFrames(string fault)
    {
        var registry = new MediaPreviewRegistry(new Authorization());
        var provider = new Provider { Fault = fault };
        await using var scope = await Activate(registry, provider);
        using var preview = registry.Open(Context(), "fixture", "preview");
        Assert.NotNull(await Record.ExceptionAsync(() => preview.CaptureAsync().AsTask()));
        Assert.True(provider.LastStream!.Disposed);
        provider.Fault = null;
        Assert.Equal(Png, (await preview.CaptureAsync()).Png.ToArray());
    }

    private static ValueTask<ExtensionScope> Activate(MediaPreviewRegistry registry, Provider provider, bool fails = false,
        Action? afterRegistration = null, Action<MediaRegistrationPort>? retain = null, bool declared = true)
    {
        var manifest = Encoding.UTF8.GetBytes($$$"""
            {"format":"nexus.extension.manifest","version":1,"extensionId":"fixture","artifactName":"Fixture",
            "displayName":"Fixture","packageVersion":"0.5.0","kind":"managed-code","minHostVersion":"0.18.0",
            "sdkVersion":"0.1.0","contractFingerprint":"{{{SdkIdentity.ContractFingerprint}}}","capabilities":{{{(declared ? "[\"media.capture\"]" : "[]")}}},
            "permissions":{},"managed":{"entryAssembly":"Fixture.dll","entryType":"Fixture.Extension"}}
            """);
        var reader = new ContractReader();
        return new ExtensionActivator(reader).ActivateManagedAsync(manifest, "fixture", (descriptor, scope) =>
        {
            var port = new MediaRegistrationPort(descriptor, scope, registry, reader);
            retain?.Invoke(port);
            return new Extension(port, provider, fails, afterRegistration);
        }, default);
    }

    private sealed class Extension(IMediaPreviewRegistrationPort port, IMediaFrameProvider provider, bool fails, Action? after) : IExtension
    {
        public async ValueTask ActivateAsync(IExtensionScope scope, CancellationToken cancellationToken)
        {
            await port.RegisterAsync("preview", provider, cancellationToken);
            after?.Invoke();
            if (fails) throw new InvalidOperationException("controlled_activation_failure");
        }
    }

    private sealed class Authorization : IRunMediaAuthorization
    {
        public bool Allowed { get; set; } = true;
        public void AssertAllowed(RunMediaContext context, string extensionId)
        {
            if (!Allowed) throw new UnauthorizedAccessException("run_media_access_denied");
        }
    }

    private sealed class Provider : IMediaFrameProvider
    {
        public bool Block { get; init; }
        public string? Fault { get; set; }
        public int Calls { get; private set; }
        public MediaCapture? LastRequest { get; private set; }
        public TrackedStream? LastStream { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<MediaFrameContent> CaptureAsync(MediaCapture request, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            Entered.TrySetResult();
            if (Block) await Release.Task;
            var capture = request;
            if (Fault == "request") capture = capture with { RequestId = Guid.NewGuid().ToString("D") };
            if (Fault == "run") capture = capture with { RunId = Guid.NewGuid().ToString("D") };
            if (Fault == "instance") capture = capture with { InstanceId = Guid.NewGuid().ToString("D") };
            if (Fault == "attempt") capture = capture with { AttemptId = Guid.NewGuid().ToString("D") };
            if (Fault == "preview") capture = capture with { PreviewId = Guid.NewGuid().ToString("D") };
            if (Fault == "sequence") capture = capture with { Sequence = request.Sequence + 1 };
            var bytes = Fault switch { "excess" => Png.Concat(new byte[1]).ToArray(), "truncated" => Png[..^1], _ => Png };
            LastStream = new TrackedStream(bytes);
            return new MediaFrameContent(new MediaFrame
            {
                CapturedAt = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                Format = "nexus.media.frame", Version = Fault == "generation" ? 2 : 1, Capture = capture,
                MimeType = "image/png", Width = Fault == "dimensions" ? 2 : 1, Height = 1,
                SizeBytes = Fault == "budget" ? 8388609 : Png.Length,
                Sha256 = Fault == "digest" ? new string('0', 64) : Convert.ToHexStringLower(SHA256.HashData(Png))
            }, LastStream);
        }
    }

    private sealed class TrackedStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
