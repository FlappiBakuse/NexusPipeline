using System.Text.Json;
using NexusPipeline.Extensibility.Frontend;
using NexusPipeline.Extensibility.Lifecycle;
using NexusPipeline.Extensibility.State;

namespace Controlled;

internal static class ControlledImages
{
    internal static async ValueTask<FrontendResult> InvokeAsync(ExtensionActivationContext context,
        FrontendCall call, CancellationToken cancellation)
    {
        using var document = JsonDocument.Parse(call.Payload); var input = document.RootElement;
        var images = context.Images ?? throw new IOException("fixture_images_unavailable");
        object result = call.Operation switch
        {
            "image-list" => await images.ListAsync(input.GetProperty("name").GetString()!, cancellation),
            "image-read" => await images.ReadAsync(input.GetProperty("id").GetString()!, cancellation),
            "image-write" => await images.WriteAsync(call.OperationId!.Value, input.GetProperty("name").GetString()!,
                input.GetProperty("revision").GetInt64(), JsonSerializer.SerializeToUtf8Bytes(input.GetProperty("value")),
                input.GetProperty("ids").EnumerateArray().Select(item => item.GetString()!).ToArray(),
                input.TryGetProperty("addition", out var addition) && addition.ValueKind != JsonValueKind.Null ?
                    new ExtensionImageInput(addition.GetProperty("type").GetString()!, Convert.FromBase64String(addition.GetProperty("content").GetString()!)) : null,
                cancellation),
            _ => throw new IOException("fixture_image_operation_unknown")
        };
        return new("application/json", JsonSerializer.SerializeToUtf8Bytes(result));
    }
}
