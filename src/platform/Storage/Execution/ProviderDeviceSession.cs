using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Core.Media;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class ProviderDeviceSession(PcTargetStage? pc, StepLiveMedia media, Func<bool> expired, AndroidDeviceInput? android = null)
{
    private PcDeviceInput? _pc = pc is null ? null : new(pc, expired);
    private (string Capture, string Mouse, string Keyboard) _methods = ("printWindow", "sendMessage", "sendMessage");
    private bool _used;
    private PreviewImage? _frame;
    private string? _frameId, _sha256;

    internal async ValueTask<ExecutionDeviceResponse> InvokeAsync(ExecutionDeviceRequest request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (expired()) throw new IOException("execution_device_budget_expired");
        ProviderDevicePolicy.Require(request);
        var response = new ExecutionDeviceResponse { Sequence = request.Sequence, Kind = request.Kind, Success = true };
        if (request.Kind == "configure")
        {
            if (request.CaptureMethod == "adb" ? android is null : pc is null) return response with { Success = false };
            var selected = (request.CaptureMethod!, request.MouseMethod!, request.KeyboardMethod!);
            if (_used && selected != _methods) throw new IOException("execution_device_configuration_frozen");
            if (selected != _methods)
            {
                _pc?.Close(); _methods = selected;
                _pc = pc is null ? null : new(pc, expired, _methods.Mouse, _methods.Keyboard);
            }
            return response;
        }
        _used = true;
        if (request.Kind == "capture")
        {
            _frame = null; _frameId = null; _sha256 = null;
            if (_methods.Capture == "screenDc") (_pc ?? throw new IOException("execution_device_target_unavailable")).Present(cancellation);
            var frame = await media.CaptureDeviceAsync(cancellation, _methods.Capture).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            if (expired()) throw new IOException("execution_device_budget_expired");
            _frame = frame; _frameId = Guid.NewGuid().ToString("N"); _sha256 = Convert.ToHexStringLower(SHA256.HashData(frame.Png.Span));
            return response with { FrameId = _frameId, Sha256 = _sha256, Width = frame.Width, Height = frame.Height, SizeBytes = frame.SizeBytes };
        }
        if (request.Kind == "image")
        {
            var frame = _frame ?? throw new IOException("execution_device_frame_expired");
            if (request.FrameId != _frameId || request.Offset is null or < 0 || request.Offset >= frame.SizeBytes)
                throw new IOException("execution_device_frame_expired");
            var offset = checked((int)request.Offset.Value);
            return response with { FrameId = _frameId, Offset = offset, SizeBytes = frame.SizeBytes, Sha256 = _sha256,
                ContentBase64 = Convert.ToBase64String(frame.Png.Span.Slice(offset, Math.Min(131072, frame.SizeBytes - offset))) };
        }
        if (request.Kind == "features") return response with { Features = 0 };
        if (android is not null) return await android.InvokeAsync(request, cancellation).ConfigureAwait(false);
        return response with { Success = _pc is not null && await _pc.InvokeAsync(request, cancellation).ConfigureAwait(false) };
    }
    internal void Close() { _pc?.Close(); android?.Close(); _frame = null; _frameId = null; _sha256 = null; }
}
