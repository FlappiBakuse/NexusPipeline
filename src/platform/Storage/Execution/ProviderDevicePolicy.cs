using System.Text.Json;
using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class ProviderDevicePolicy
{
    internal static void Require(ExecutionDeviceRequest request)
    {
        if (request.Kind == "configure" && (request.CaptureMethod == "adb"
            ? request.MouseMethod != "adb" || request.KeyboardMethod != "adb"
            : request.CaptureMethod is not ("printWindow" or "gdi" or "screenDc" or "framePool") ||
            request.MouseMethod is not ("sendMessage" or "postMessage" or "sendMessageWithCursorPos" or "seize") ||
            request.KeyboardMethod is not ("sendMessage" or "postMessage" or "sendMessageWithCursorPos" or "seize")))
            throw new IOException("execution_device_methods_invalid");
        string[] arguments = request.Kind switch {
            "configure" => ["captureMethod", "mouseMethod", "keyboardMethod"],
            "connect" or "connected" or "features" or "capture" or "inactive" => [],
            "image" => ["frameId", "offset"], "click" => ["x", "y"],
            "swipe" => ["x", "y", "x2", "y2", "durationMilliseconds"],
            "touch" => request.Phase == 2 ? ["phase", "contact"] : ["phase", "contact", "x", "y", "pressure"],
            "key" => ["phase", "keyCode"], "text" => ["text"], "scroll" or "move" => ["dx", "dy"],
            "startApp" or "stopApp" => ["intent"], "shell" => ["command", "timeoutMilliseconds"],
            _ => throw new IOException("execution_device_action_invalid") };
        var fields = JsonSerializer.SerializeToElement(request).EnumerateObject().Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        if (!fields.SetEquals(arguments.Prepend("kind").Prepend("sequence")) || request.Sequence is < 1 or > 100000 ||
            request.Text?.Contains('\0') == true || request.Intent?.Any(char.IsControl) == true || request.Command?.Contains('\0') == true)
            throw new IOException("execution_device_arguments_invalid");
    }
}
