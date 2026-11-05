using System.Text.Json;
using NexusPipeline.Extensibility.Frontend;
using NexusPipeline.Extensibility.Lifecycle;

namespace Controlled;

internal static class ControlledBrowserLogin
{
    internal static async ValueTask<FrontendResult> CaptureAsync(ExtensionActivationContext context, FrontendCall call, string directory, CancellationToken cancellation)
    {
        var path = Path.Combine(directory, "browser-capture-origin");
        var origin = File.Exists(path) ? File.ReadAllText(path) : "https://login.example.test";
        using var input = JsonDocument.Parse(call.Payload);
        var incomplete = input.RootElement.TryGetProperty("incomplete", out var selected) && selected.GetBoolean();
        var policy = new BrowserLoginPolicy("fixture", origin + (incomplete ? "/login-incomplete" : "/login-fixture"), [origin], [],
            [new("token", new Uri(origin).IdnHost, "/", "token")], [], [], [["token"]], [new("token", "token", [], 128)], []);
        BrowserLoginCapture capture;
        try { capture = await context.Desktop!.CaptureAsync(policy, cancellation); }
        finally { File.WriteAllText(Path.Combine(directory, "browser-ended-" + call.OperationId), "ended"); }
        var valid = false; var jsonDenied = false; var disposed = false;
        try
        {
            capture.Values["token"].Use(value => valid = value.SequenceEqual("synthetic"));
            try { _ = JsonSerializer.Serialize(capture); }
            catch (NotSupportedException) { jsonDenied = true; }
        }
        finally { capture.Dispose(); }
        try { capture.Values["token"].Use(_ => { }); }
        catch (ObjectDisposedException) { disposed = true; }
        return new("application/json", JsonSerializer.SerializeToUtf8Bytes(new { valid, jsonDenied, disposed }));
    }
}
